// Язык запросов: выполнение пакета над таблицами в памяти.
// План: привязка полей к колонкам (Bind) → источники и соединения → ГДЕ → группировка и агрегаты
// → ИМЕЮЩИЕ → РАЗЛИЧНЫЕ → ОБЪЕДИНИТЬ → УПОРЯДОЧИТЬ ПО → ПЕРВЫЕ.
// Строка — массив BslValue на все колонки всех источников; пустая сторона внешнего соединения — NULL.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OneScript.StandardLibrary.Collections;
using OneScript.StandardLibrary.Collections.ValueList;
using OneScript.StandardLibrary.Collections.ValueTable;
using OneScript.Values;
using ScriptEngine.Machine;

namespace OneScriptWasm.Query
{
    /// <summary>Таблица в памяти: результат запроса, временная таблица, таблица-параметр.</summary>
    public sealed class QueryTable
    {
        public List<string> Columns = new();
        public List<BslValue[]> Rows = new();
    }

    /// <summary>
    /// Подключаемый источник именованных таблиц (Справочник.Номенклатура, РегистрНакопления.Х.Остатки(…)).
    /// Сейчас не зарегистрирован ни один: в песочнице нет информационной базы. Задел для демобазы.
    /// </summary>
    public interface IQueryTableProvider
    {
        bool TryGetTable(string name, IReadOnlyList<BslValue> args, out QueryTable table);
    }

    internal sealed class ScopeColumn
    {
        public string Source;   // псевдоним источника
        public string Name;
    }

    internal sealed class Scope
    {
        public List<ScopeColumn> Columns = new();
        public int Visible;          // сколько колонок видно (условие ПО видит только присоединённые источники)
        public Scope Parent;         // внешний запрос (для коррелированного подзапроса в В)
        public QueryNode Owner;      // подзапрос, которому принадлежит область
        public HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class SourcePlan
    {
        public SourceRef Ref;
        public QueryTable Data;
        public int Offset;
        public bool StartsItem;     // первый источник элемента ИЗ (через запятую — декартово произведение)
        public JoinKind Join;
        public Expr On;
        public List<(Expr Left, Expr Right)> HashKeys;
    }

    internal sealed class CorePlan
    {
        public SelectCore Core;
        public Scope Scope;
        public int Width;
        public List<SourcePlan> Sources = new();
        public List<Expr> Fields = new();
        public List<string> Names = new();
        public bool Grouped;
        public List<Expr> GroupExprs = new();
        public List<Expr> Hidden = new();   // выражения УПОРЯДОЧИТЬ ПО, которых нет в списке выборки
        public List<int> OrderColumns;      // колонка выходной строки для каждого элемента УПОРЯДОЧИТЬ ПО
    }

    internal sealed class QueryPlan
    {
        public QueryNode Node;
        public List<CorePlan> Cores = new();
        public List<string> Names;
        public List<(int Column, bool Desc)> Order = new();
    }

    internal sealed class QueryRun
    {
        public const int MaxRows = 2_000_000;

        private readonly Func<string, BslValue> _getParam;
        private readonly Dictionary<string, QueryTable> _temps;
        private readonly IReadOnlyList<IQueryTableProvider> _providers;
        private readonly Dictionary<string, Regex> _likeCache = new();
        private readonly Dictionary<InExpr, QueryPlan> _subPlans = new();
        private readonly Dictionary<InExpr, HashSet<BslValue[]>> _subCache = new();
        private long _produced;

        public QueryRun(Func<string, BslValue> getParam, Dictionary<string, QueryTable> temps, IReadOnlyList<IQueryTableProvider> providers)
        {
            _getParam = getParam;
            _temps = temps;
            _providers = providers ?? Array.Empty<IQueryTableProvider>();
        }

        /// <summary>Результат оператора пакета: таблица (ВЫБРАТЬ, ПОМЕСТИТЬ) или null (УНИЧТОЖИТЬ).</summary>
        public sealed class StatementResult
        {
            public QueryTable Table;
            public QueryTable Placed;   // содержимое временной таблицы для ВыполнитьПакетСПромежуточнымиДанными
        }

        public List<StatementResult> ExecuteBatch(List<Statement> batch)
        {
            var results = new List<StatementResult>();
            foreach (var st in batch)
            {
                if (st is DropStatement drop)
                {
                    if (!_temps.Remove(drop.Name))
                        throw new QueryException($"Таблица не найдена \"{drop.Name}\"", drop.Pos, drop.Len);
                    results.Add(null);
                    continue;
                }
                var q = ((SelectStatement)st).Query;
                var plan = BindQuery(q, null);
                var data = Execute(plan, null);
                if (q.Into != null)
                {
                    if (_temps.ContainsKey(q.Into))
                        throw new QueryException($"Таблица \"{q.Into}\" уже существует", q.IntoPos, q.Into.Length);
                    _temps[q.Into] = data;
                    var count = new QueryTable();
                    count.Columns.Add("Количество");
                    count.Rows.Add(new[] { QV.Num(data.Rows.Count) });
                    results.Add(new StatementResult { Table = count, Placed = data });
                }
                else
                {
                    results.Add(new StatementResult { Table = data });
                }
            }
            return results;
        }

        // ---------- Параметры ----------

        private BslValue Param(string name, int pos, int len)
        {
            var v = _getParam(name);
            if (v == null) throw new QueryException($"Не задано значение параметра \"{name}\"", pos, len);
            return v;
        }

        private static bool IsCollection(BslValue v) =>
            v is ArrayImpl || v is FixedArrayImpl || v is ValueListImpl || v is ValueTable || v is QueryResultContext;

        public BslValue ScalarParam(string name, int pos, int len)
        {
            var v = Param(name, pos, len);
            if (IsCollection(v))
                throw new QueryException($"Неверный тип параметра \"{name}\": {QV.TypeName(v)} можно использовать только в условии В (&{name}) или как источник ИЗ &{name}", pos, len);
            return v;
        }

        public IEnumerable<BslValue> ListParam(string name, int pos, int len)
        {
            var v = Param(name, pos, len);
            switch (v)
            {
                case ArrayImpl arr: return arr.Select(x => QV.Raw(x));
                case FixedArrayImpl fixedArr: return fixedArr.Select(x => QV.Raw(x));
                case ValueListImpl list: return list.Select(i => QV.Raw(i.Value));
                case ValueTable vt:
                    if (vt.Columns.Count() == 0) return Array.Empty<BslValue>();
                    var first = new List<BslValue>();
                    foreach (ValueTableRow r in vt) first.Add(QV.Raw(r.Get(0)));
                    return first;
                case QueryResultContext qr:
                    return qr.Data.Columns.Count == 0 ? Array.Empty<BslValue>() : qr.Data.Rows.Select(r => r[0]);
                default: return new[] { v };
            }
        }

        public Regex LikeRegex(string pattern, char? escape, Expr at)
        {
            var key = (escape?.ToString() ?? "") + "\u0001" + pattern;
            if (!_likeCache.TryGetValue(key, out var re))
            {
                re = LikeExpr.BuildRegex(pattern, escape, at);
                _likeCache[key] = re;
            }
            return re;
        }

        public HashSet<BslValue[]> SubquerySet(InExpr e, EvalCtx c)
        {
            if (_subCache.TryGetValue(e, out var cached)) return cached;
            var plan = _subPlans[e];
            var data = Execute(plan, plan.Node.Correlated ? c : null);
            var set = new HashSet<BslValue[]>(data.Rows.Select(r => new[] { r[0] }), RowKeyComparer.Instance);
            if (!plan.Node.Correlated) _subCache[e] = set;
            return set;
        }

        private void Count(long n)
        {
            _produced += n;
            if (_produced > MaxRows)
                throw new QueryException($"Слишком большой результат: больше {MaxRows:N0} промежуточных строк. Проверьте условия соединений", 0, 0);
        }

        // ---------- Привязка ----------

        private QueryPlan BindQuery(QueryNode q, Scope parent)
        {
            var plan = new QueryPlan { Node = q };
            bool single = q.Cores.Count == 1;
            foreach (var core in q.Cores)
                plan.Cores.Add(BindCore(core, parent, q, single ? q.OrderBy : null));
            plan.Names = plan.Cores[0].Names;
            for (int i = 1; i < plan.Cores.Count; i++)
            {
                if (plan.Cores[i].Fields.Count != plan.Names.Count)
                    throw new QueryException("Различное количество полей в объединяемых запросах", q.Cores[i].Pos, 7);
            }
            if (single)
            {
                var cp = plan.Cores[0];
                for (int i = 0; i < q.OrderBy.Count; i++)
                    plan.Order.Add((cp.OrderColumns[i], q.OrderBy[i].Desc));
            }
            else
            {
                // В объединении УПОРЯДОЧИТЬ ПО ссылается на имена колонок результата
                foreach (var item in q.OrderBy)
                {
                    int col = -1;
                    if (item.Expr is FieldExpr f && f.Parts.Length == 1)
                        col = plan.Names.FindIndex(n => n.Equals(f.Parts[0], StringComparison.OrdinalIgnoreCase));
                    if (col < 0)
                    {
                        var name = item.Expr is FieldExpr fe ? fe.Display : "выражение";
                        throw new QueryException($"Поле не найдено \"{name}\": в объединении упорядочивают по именам колонок результата", item.Expr.Pos, item.Expr.Len);
                    }
                    plan.Order.Add((col, item.Desc));
                }
            }
            return plan;
        }

        private CorePlan BindCore(SelectCore core, Scope parent, QueryNode owner, List<OrderItem> orderBy)
        {
            var scope = new Scope { Parent = parent, Owner = owner };
            var cp = new CorePlan { Core = core, Scope = scope };

            // Источники
            foreach (var item in core.From)
            {
                AddSource(cp, item.Source, true, JoinKind.Inner, null);
                foreach (var j in item.Joins)
                {
                    var sp = AddSource(cp, j.Source, false, j.Kind, j.On);
                    scope.Visible = scope.Columns.Count;
                    Bind(j.On, scope, "ПО", false);
                    sp.HashKeys = HashKeys(j.On, sp.Offset, sp.Offset + sp.Data.Columns.Count);
                }
            }
            scope.Visible = scope.Columns.Count;
            cp.Width = scope.Columns.Count;

            if (core.Where != null) Bind(core.Where, scope, "ГДЕ", false);

            // Список выборки
            var explicitAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            int autoNumber = 0;
            foreach (var f in core.Fields)
            {
                if (f.IsStar)
                {
                    bool any = false;
                    for (int i = 0; i < scope.Columns.Count; i++)
                    {
                        var col = scope.Columns[i];
                        if (f.StarSource != null && !col.Source.Equals(f.StarSource, StringComparison.OrdinalIgnoreCase)) continue;
                        any = true;
                        cp.Fields.Add(new FieldExpr { Parts = new[] { col.Source, col.Name }, Depth = 0, Index = i, Pos = f.Pos, Len = f.Len });
                        names.Add(Unique(names, col.Name));
                    }
                    if (f.StarSource != null && !any && !scope.Sources.Contains(f.StarSource))
                        throw new QueryException($"Таблица не найдена \"{f.StarSource}\"", f.Pos, f.Len);
                    continue;
                }
                Bind(f.Expr, scope, null, true);
                cp.Fields.Add(f.Expr);
                if (f.Alias != null)
                {
                    if (!explicitAliases.Add(f.Alias))
                        throw new QueryException($"Дублирование псевдонима поля \"{f.Alias}\"", f.Pos, f.Len);
                    if (names.Any(n => n.Equals(f.Alias, StringComparison.OrdinalIgnoreCase)))
                        throw new QueryException($"Дублирование псевдонима поля \"{f.Alias}\"", f.Pos, f.Len);
                    names.Add(f.Alias);
                }
                else
                {
                    var auto = AutoName(f.Expr) ?? "Поле" + (++autoNumber);
                    names.Add(Unique(names, auto));
                }
            }
            cp.Names = names;

            // Группировка
            bool hasAgg = cp.Fields.Any(ContainsAgg) || (core.Having != null && ContainsAgg(core.Having))
                          || (orderBy != null && orderBy.Any(o => ContainsAgg(o.Expr)));
            if (core.GroupBy != null)
            {
                foreach (var g in core.GroupBy)
                {
                    Bind(g, scope, "СГРУППИРОВАТЬ ПО", false);
                    cp.GroupExprs.Add(g);
                }
            }
            cp.Grouped = core.GroupBy != null || hasAgg || core.Having != null;
            var groupKeys = new HashSet<string>(cp.GroupExprs.Select(g => g.Key()));
            if (core.Having != null) Bind(core.Having, scope, null, true);
            if (cp.Grouped)
            {
                foreach (var e in cp.Fields) CheckGrouped(e, groupKeys);
                if (core.Having != null) CheckGrouped(core.Having, groupKeys);
            }

            // УПОРЯДОЧИТЬ ПО (для запроса без объединения): псевдоним колонки, иначе выражение над источниками
            cp.OrderColumns = new List<int>();
            if (orderBy != null)
            {
                foreach (var item in orderBy)
                {
                    int col = -1;
                    if (item.Expr is FieldExpr f && f.Parts.Length == 1)
                        col = names.FindIndex(n => n.Equals(f.Parts[0], StringComparison.OrdinalIgnoreCase));
                    if (col < 0)
                    {
                        Bind(item.Expr, scope, null, true);
                        if (cp.Grouped) CheckGrouped(item.Expr, groupKeys);
                        var key = item.Expr.Key();
                        col = cp.Fields.FindIndex(e => e.Key() == key);
                        if (col < 0)
                        {
                            col = cp.Fields.Count + cp.Hidden.Count;
                            cp.Hidden.Add(item.Expr);
                        }
                    }
                    cp.OrderColumns.Add(col);
                }
            }
            return cp;
        }

        private SourcePlan AddSource(CorePlan cp, SourceRef src, bool startsItem, JoinKind kind, Expr on)
        {
            var scope = cp.Scope;
            if (!scope.Sources.Add(src.Alias))
                throw new QueryException($"Дублирование псевдонима источника \"{src.Alias}\"", src.Pos, src.Len);
            var data = ResolveSource(src);
            var sp = new SourcePlan { Ref = src, Data = data, Offset = scope.Columns.Count, StartsItem = startsItem, Join = kind, On = on };
            foreach (var c in data.Columns) scope.Columns.Add(new ScopeColumn { Source = src.Alias, Name = c });
            cp.Sources.Add(sp);
            return sp;
        }

        private QueryTable ResolveSource(SourceRef src)
        {
            switch (src.Kind)
            {
                case SourceKind.Param:
                {
                    var v = Param(src.Name, src.Pos, src.Len);
                    if (v is ValueTable vt) return FromValueTable(vt, src);
                    if (v is QueryResultContext qr) return qr.Data;
                    throw new QueryException($"Неверный тип параметра \"{src.Name}\": источником ИЗ может быть ТаблицаЗначений, получено {QV.TypeName(v)}", src.Pos, src.Len);
                }
                case SourceKind.Subquery:
                {
                    // Вложенный запрос в ИЗ не видит полей внешнего запроса
                    var plan = BindQuery(src.Sub, null);
                    return Execute(plan, null);
                }
                default:
                {
                    if (!src.Name.Contains('.') && _temps.TryGetValue(src.Name, out var temp) && src.Args == null) return temp;
                    var args = src.Args?.Select(a => a == null ? QV.Undefined : a.Eval(new EvalCtx { Run = this })).ToList() ?? new List<BslValue>();
                    foreach (var p in _providers)
                        if (p.TryGetTable(src.Name, args, out var t)) return t;
                    var hint = src.Name.Contains('.')
                        ? " (в песочнице нет информационной базы: запрос читает таблицы значений из параметров — ИЗ &Таблица КАК Т — и временные таблицы)"
                        : "";
                    throw new QueryException($"Таблица не найдена \"{src.Name}\"{hint}", src.Pos, src.Len);
                }
            }
        }

        private static QueryTable FromValueTable(ValueTable vt, SourceRef src)
        {
            var t = new QueryTable();
            var cols = new List<ValueTableColumn>();
            foreach (ValueTableColumn c in vt.Columns)
            {
                cols.Add(c);
                t.Columns.Add(c.Name);
            }
            foreach (ValueTableRow r in vt)
            {
                var row = new BslValue[cols.Count];
                for (int i = 0; i < cols.Count; i++)
                {
                    var v = QV.Raw(r.Get(cols[i]));
                    if (!QV.IsSelectable(v))
                        throw new QueryException($"Тип не может быть выбран в запросе: колонка \"{cols[i].Name}\" таблицы &{src.Name} содержит значение типа {QV.TypeName(v)}", src.Pos, src.Len);
                    row[i] = v;
                }
                t.Rows.Add(row);
            }
            return t;
        }

        private static string Unique(List<string> names, string name)
        {
            if (!names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
            for (int k = 1; ; k++)
            {
                var candidate = name + k;
                if (!names.Any(n => n.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
            }
        }

        private static string AutoName(Expr e) => e switch
        {
            FieldExpr f => f.Parts[^1],
            AggExpr { Arg: FieldExpr f } => f.Parts[^1],
            FuncExpr { Name: "ISNULL" } fn when fn.Args[0] is FieldExpr f => f.Parts[^1],
            CastExpr { E: FieldExpr f } => f.Parts[^1],
            _ => null,
        };

        private static bool ContainsAgg(Expr e)
        {
            if (e is AggExpr) return true;
            foreach (var c in e.Children()) if (c != null && ContainsAgg(c)) return true;
            return false;
        }

        /// <summary>Поле вне агрегата должно входить в СГРУППИРОВАТЬ ПО (или быть выражением группировки).</summary>
        private static void CheckGrouped(Expr e, HashSet<string> groupKeys)
        {
            if (e is AggExpr) return;
            if (groupKeys.Contains(e.Key())) return;
            if (e is FieldExpr f && f.Depth == 0)
                throw new QueryException($"Поле не входит в группу \"{f.Display}\"", f.PartPos, f.Parts[^1].Length);
            foreach (var c in e.Children()) if (c != null) CheckGrouped(c, groupKeys);
        }

        /// <summary>Привязывает поля к колонкам. clause — где агрегаты запрещены (ГДЕ, ПО, СГРУППИРОВАТЬ ПО).</summary>
        private void Bind(Expr e, Scope scope, string clause, bool aggAllowed, bool insideAgg = false)
        {
            switch (e)
            {
                case FieldExpr f:
                    ResolveField(f, scope);
                    return;
                case AggExpr a:
                    if (clause != null)
                        throw new QueryException($"Агрегатная функция {a.Display} не может использоваться в {clause}", a.Pos, a.Len);
                    if (insideAgg)
                        throw new QueryException($"Вложенные агрегатные функции не допускаются: {a.Display}", a.Pos, a.Len);
                    if (a.Arg != null) Bind(a.Arg, scope, clause, aggAllowed, true);
                    return;
                case InExpr { Sub: not null } inSub:
                    Bind(inSub.E, scope, clause, aggAllowed, insideAgg);
                    var subScopeParent = scope;
                    _subPlans[inSub] = BindQuery(inSub.Sub, subScopeParent);
                    return;
            }
            foreach (var c in e.Children())
                if (c != null) Bind(c, scope, clause, aggAllowed, insideAgg);
        }

        private static void ResolveField(FieldExpr f, Scope start)
        {
            int depth = 0;
            for (var s = start; s != null; s = s.Parent, depth++)
            {
                int found = -1, count = 0;
                bool sourceExists = false;
                var visible = s.Visible;
                if (f.Parts.Length == 1)
                {
                    for (int i = 0; i < visible; i++)
                        if (s.Columns[i].Name.Equals(f.Parts[0], StringComparison.OrdinalIgnoreCase)) { found = i; count++; }
                    if (count > 1)
                        throw new QueryException($"Неоднозначное поле \"{f.Display}\": оно есть в нескольких источниках, укажите псевдоним источника", f.Pos, f.Len);
                }
                else if (f.Parts.Length == 2)
                {
                    sourceExists = s.Sources.Contains(f.Parts[0]);
                    for (int i = 0; i < visible; i++)
                    {
                        var c = s.Columns[i];
                        if (c.Source.Equals(f.Parts[0], StringComparison.OrdinalIgnoreCase) && c.Name.Equals(f.Parts[1], StringComparison.OrdinalIgnoreCase))
                        { found = i; break; }
                    }
                }
                else
                {
                    sourceExists = s.Sources.Contains(f.Parts[0]);
                }
                if (found >= 0)
                {
                    f.Depth = depth;
                    f.Index = found;
                    // Подзапросы, через которые прошли наружу, зависят от внешней строки
                    var x = start;
                    for (int d = 0; d < depth; d++, x = x.Parent) if (x.Owner != null) x.Owner.Correlated = true;
                    return;
                }
                if (sourceExists) break;
            }
            var hint = f.Parts.Length > 2 ? " (обращение к реквизитам через точку есть только у ссылок информационной базы)" : "";
            throw new QueryException($"Поле не найдено \"{f.Display}\"{hint}", f.PartPos, f.Parts[^1].Length);
        }

        /// <summary>Пары «левое = правое» из условия соединения для хеш-соединения.</summary>
        private static List<(Expr, Expr)> HashKeys(Expr on, int from, int to)
        {
            var keys = new List<(Expr, Expr)>();
            foreach (var c in SplitAnd(on))
            {
                if (c is not CmpExpr { Op: "=" } cmp) continue;
                var l = Columns(cmp.L);
                var r = Columns(cmp.R);
                if (l == null || r == null || l.Count == 0 || r.Count == 0) continue;
                bool lLeft = l.All(i => i < from), lRight = l.All(i => i >= from && i < to);
                bool rLeft = r.All(i => i < from), rRight = r.All(i => i >= from && i < to);
                if (lLeft && rRight) keys.Add((cmp.L, cmp.R));
                else if (lRight && rLeft) keys.Add((cmp.R, cmp.L));
            }
            return keys;
        }

        private static IEnumerable<Expr> SplitAnd(Expr e)
        {
            if (e is AndExpr a)
            {
                foreach (var x in SplitAnd(a.L)) yield return x;
                foreach (var x in SplitAnd(a.R)) yield return x;
            }
            else yield return e;
        }

        /// <summary>Колонки текущего уровня в выражении; null — выражение не годится для ключа соединения.</summary>
        private static List<int> Columns(Expr e)
        {
            var list = new List<int>();
            bool ok = true;
            void Walk(Expr x)
            {
                switch (x)
                {
                    case FieldExpr f:
                        if (f.Depth == 0) list.Add(f.Index); else ok = false;
                        return;
                    case AggExpr:
                    case InExpr { Sub: not null }:
                        ok = false;
                        return;
                }
                foreach (var c in x.Children()) if (c != null) Walk(c);
            }
            Walk(e);
            return ok ? list : null;
        }

        // ---------- Выполнение ----------

        private QueryTable Execute(QueryPlan plan, EvalCtx outer)
        {
            var q = plan.Node;
            List<BslValue[]> rows;
            if (plan.Cores.Count == 1)
            {
                var cp = plan.Cores[0];
                rows = RunCore(cp, outer);
                if (cp.Core.Distinct) rows = Distinct(rows);
                Sort(rows, plan.Order);
                if (cp.Core.Top is int top && rows.Count > top) rows = rows.GetRange(0, top);
                int visible = cp.Fields.Count;
                if (cp.Hidden.Count > 0) rows = rows.Select(r => r.Take(visible).ToArray()).ToList();
            }
            else
            {
                rows = null;
                for (int i = 0; i < plan.Cores.Count; i++)
                {
                    var cp = plan.Cores[i];
                    var part = RunCore(cp, outer);
                    if (cp.Core.Distinct) part = Distinct(part);
                    if (cp.Core.Top is int top && part.Count > top) part = part.GetRange(0, top);
                    if (rows == null) { rows = part; continue; }
                    rows.AddRange(part);
                    Count(part.Count);
                    // ОБЪЕДИНИТЬ без ВСЕ убирает повторы во всём накопленном результате
                    if (!q.UnionAll[i - 1]) rows = Distinct(rows);
                }
                Sort(rows, plan.Order);
            }
            return new QueryTable { Columns = plan.Names.ToList(), Rows = rows };
        }

        private static List<BslValue[]> Distinct(List<BslValue[]> rows)
        {
            var seen = new HashSet<BslValue[]>(RowKeyComparer.Instance);
            var result = new List<BslValue[]>(rows.Count);
            foreach (var r in rows) if (seen.Add(r)) result.Add(r);
            return result;
        }

        private static void Sort(List<BslValue[]> rows, List<(int Column, bool Desc)> order)
        {
            if (order.Count == 0 || rows.Count < 2) return;
            // Устойчивая сортировка: при равных ключах сохраняется исходный порядок
            var indexed = rows.Select((r, i) => (r, i)).ToList();
            indexed.Sort((a, b) =>
            {
                foreach (var (col, desc) in order)
                {
                    int c = QV.Compare(a.r[col], b.r[col]);
                    if (c != 0) return desc ? -c : c;
                }
                return a.i.CompareTo(b.i);
            });
            for (int i = 0; i < rows.Count; i++) rows[i] = indexed[i].r;
        }

        private BslValue[] NewRow(int width)
        {
            var r = new BslValue[width];
            Array.Fill(r, QV.Null);
            return r;
        }

        private List<BslValue[]> RunCore(CorePlan cp, EvalCtx outer)
        {
            var ctx = new EvalCtx { Outer = outer, Run = this };
            int width = cp.Width;

            // ИЗ: декартово произведение элементов, внутри элемента — соединения
            List<BslValue[]> rows;
            if (cp.Sources.Count == 0)
            {
                rows = new List<BslValue[]> { NewRow(0) };
            }
            else
            {
                rows = null;
                List<BslValue[]> item = null;
                foreach (var sp in cp.Sources)
                {
                    if (sp.StartsItem)
                    {
                        if (item != null) rows = rows == null ? item : Cross(rows, item, width);
                        item = SourceRows(sp, width);
                    }
                    else
                    {
                        item = Join(item, sp, width, ctx);
                    }
                }
                rows = rows == null ? item : Cross(rows, item, width);
            }

            // ГДЕ
            if (cp.Core.Where != null)
            {
                var filtered = new List<BslValue[]>();
                foreach (var r in rows)
                {
                    ctx.Row = r;
                    if (Logic.ToBool(cp.Core.Where.Eval(ctx), cp.Core.Where) == true) filtered.Add(r);
                }
                rows = filtered;
            }

            var output = new List<BslValue[]>();
            int outWidth = cp.Fields.Count + cp.Hidden.Count;
            if (!cp.Grouped)
            {
                foreach (var r in rows)
                {
                    ctx.Row = r;
                    output.Add(Project(cp, ctx, outWidth));
                }
                Count(output.Count);
                return output;
            }

            // Группировка: без СГРУППИРОВАТЬ ПО — одна группа, даже если строк нет
            var groups = new List<List<BslValue[]>>();
            if (cp.GroupExprs.Count == 0)
            {
                groups.Add(rows);
            }
            else
            {
                var index = new Dictionary<BslValue[], List<BslValue[]>>(RowKeyComparer.Instance);
                foreach (var r in rows)
                {
                    ctx.Row = r;
                    var key = new BslValue[cp.GroupExprs.Count];
                    for (int i = 0; i < key.Length; i++) key[i] = cp.GroupExprs[i].Eval(ctx);
                    if (!index.TryGetValue(key, out var g))
                    {
                        g = new List<BslValue[]>();
                        index[key] = g;
                        groups.Add(g);
                    }
                    g.Add(r);
                }
            }
            foreach (var g in groups)
            {
                // Неагрегированные поля одинаковы во всей группе: берём первую строку
                ctx.Row = g.Count > 0 ? g[0] : null;
                ctx.Group = g;
                if (cp.Core.Having != null && Logic.ToBool(cp.Core.Having.Eval(ctx), cp.Core.Having) != true) continue;
                output.Add(Project(cp, ctx, outWidth));
            }
            ctx.Group = null;
            Count(output.Count);
            return output;
        }

        private static BslValue[] Project(CorePlan cp, EvalCtx ctx, int outWidth)
        {
            var o = new BslValue[outWidth];
            for (int i = 0; i < cp.Fields.Count; i++) o[i] = cp.Fields[i].Eval(ctx);
            for (int i = 0; i < cp.Hidden.Count; i++) o[cp.Fields.Count + i] = cp.Hidden[i].Eval(ctx);
            return o;
        }

        private List<BslValue[]> SourceRows(SourcePlan sp, int width)
        {
            var list = new List<BslValue[]>(sp.Data.Rows.Count);
            foreach (var src in sp.Data.Rows)
            {
                var r = NewRow(width);
                Array.Copy(src, 0, r, sp.Offset, Math.Min(src.Length, sp.Data.Columns.Count));
                list.Add(r);
            }
            Count(list.Count);
            return list;
        }

        private List<BslValue[]> Cross(List<BslValue[]> left, List<BslValue[]> right, int width)
        {
            Count((long)left.Count * right.Count);
            var result = new List<BslValue[]>(left.Count * right.Count);
            foreach (var a in left)
                foreach (var b in right)
                {
                    var r = (BslValue[])a.Clone();
                    for (int i = 0; i < width; i++) if (!QV.IsNull(b[i])) r[i] = b[i];
                    result.Add(r);
                }
            return result;
        }

        private List<BslValue[]> Join(List<BslValue[]> left, SourcePlan sp, int width, EvalCtx ctx)
        {
            var right = SourceRows(sp, width);
            int from = sp.Offset, n = sp.Data.Columns.Count;
            var result = new List<BslValue[]>();
            var matchedRight = new bool[right.Count];

            BslValue[] Merge(BslValue[] l, BslValue[] r)
            {
                var m = (BslValue[])l.Clone();
                Array.Copy(r, from, m, from, n);
                return m;
            }

            bool OnTrue(BslValue[] row)
            {
                ctx.Row = row;
                return Logic.ToBool(sp.On.Eval(ctx), sp.On) == true;
            }

            Dictionary<BslValue[], List<int>> hash = null;
            if (sp.HashKeys.Count > 0)
            {
                hash = new Dictionary<BslValue[], List<int>>(RowKeyComparer.Instance);
                for (int j = 0; j < right.Count; j++)
                {
                    var key = HashKey(sp.HashKeys, right[j], ctx, useRight: true);
                    if (key == null) continue;   // NULL в ключе ни с чем не равен
                    if (!hash.TryGetValue(key, out var list)) hash[key] = list = new List<int>();
                    list.Add(j);
                }
            }

            foreach (var l in left)
            {
                bool matched = false;
                IEnumerable<int> candidates;
                if (hash != null)
                {
                    var key = HashKey(sp.HashKeys, l, ctx, useRight: false);
                    candidates = key != null && hash.TryGetValue(key, out var list) ? list : Enumerable.Empty<int>();
                }
                else
                {
                    candidates = Enumerable.Range(0, right.Count);
                }
                foreach (var j in candidates)
                {
                    var m = Merge(l, right[j]);
                    if (!OnTrue(m)) continue;
                    result.Add(m);
                    matched = true;
                    matchedRight[j] = true;
                    if (result.Count > MaxRows) Count(result.Count);
                }
                if (!matched && (sp.Join == JoinKind.Left || sp.Join == JoinKind.Full))
                    result.Add(l);
            }
            if (sp.Join == JoinKind.Right || sp.Join == JoinKind.Full)
            {
                for (int j = 0; j < right.Count; j++)
                    if (!matchedRight[j]) result.Add(right[j]);
            }
            Count(result.Count);
            return result;
        }

        private static BslValue[] HashKey(List<(Expr Left, Expr Right)> keys, BslValue[] row, EvalCtx ctx, bool useRight)
        {
            ctx.Row = row;
            var key = new BslValue[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                var v = (useRight ? keys[i].Right : keys[i].Left).Eval(ctx);
                if (QV.IsNull(v)) return null;
                key[i] = v;
            }
            return key;
        }
    }
}
