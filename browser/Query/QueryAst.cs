// Язык запросов: дерево разбора и вычисление выражений над строкой (или группой строк).
// Поля привязываются к колонкам при выполнении (QueryExecutor.Bind): Depth — уровень вложенности
// запроса (0 — текущий, 1 — внешний для коррелированного подзапроса), Index — номер колонки в строке.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OneScript.Values;

namespace OneScriptWasm.Query
{
    /// <summary>Контекст вычисления: текущая строка, строки группы (для агрегатов), внешний запрос.</summary>
    internal sealed class EvalCtx
    {
        public BslValue[] Row;
        public List<BslValue[]> Group;
        public EvalCtx Outer;
        public QueryRun Run;

        public EvalCtx Child(BslValue[] row) => new EvalCtx { Row = row, Outer = Outer, Run = Run };
    }

    internal abstract class Expr
    {
        public int Pos;
        public int Len;
        public abstract BslValue Eval(EvalCtx c);
        public virtual IEnumerable<Expr> Children() => Array.Empty<Expr>();
        /// <summary>Структурный ключ привязанного выражения: для сравнения с выражениями СГРУППИРОВАТЬ ПО.</summary>
        public abstract string Key();
        protected static string Keys(IEnumerable<Expr> list) => string.Join(",", list.Select(e => e?.Key() ?? "_"));
    }

    internal sealed class LitExpr : Expr
    {
        public BslValue Value;
        public override BslValue Eval(EvalCtx c) => Value;
        public override string Key() => $"L{QV.Rank(Value)}:{QV.Presentation(Value)}";
    }

    internal sealed class ParamExpr : Expr
    {
        public string Name;
        public override BslValue Eval(EvalCtx c) => c.Run.ScalarParam(Name, Pos, Len);
        public override string Key() => "P:" + Name.ToUpperInvariant();
    }

    internal sealed class FieldExpr : Expr
    {
        public string[] Parts;
        public int PartPos;     // позиция последней части имени: на неё указывает «Поле не найдено»
        public int Depth = -1;
        public int Index = -1;
        public string Display => string.Join(".", Parts);

        public override BslValue Eval(EvalCtx c)
        {
            var ctx = c;
            for (int d = 0; d < Depth; d++) ctx = ctx.Outer;
            var row = ctx.Row;
            return row == null ? QV.Null : row[Index];
        }

        public override string Key() => $"F{Depth}:{Index}";
    }

    internal sealed class UnaryExpr : Expr
    {
        public string Op;
        public Expr E;
        public override IEnumerable<Expr> Children() => new[] { E };
        public override string Key() => $"U{Op}({E.Key()})";

        public override BslValue Eval(EvalCtx c)
        {
            var v = E.Eval(c);
            if (QV.IsNull(v)) return QV.Null;
            if (v is not BslNumericValue n) throw new QueryException($"Неверные параметры \"{Op}\"", Pos, 1);
            return Op == "-" ? QV.Num(-(decimal)n) : v;
        }
    }

    internal sealed class ArithExpr : Expr
    {
        public string Op;
        public Expr L, R;
        public override IEnumerable<Expr> Children() => new[] { L, R };
        public override string Key() => $"A{Op}({L.Key()},{R.Key()})";

        public override BslValue Eval(EvalCtx c)
        {
            var a = L.Eval(c);
            var b = R.Eval(c);
            if (QV.IsNull(a) || QV.IsNull(b)) return QV.Null;
            if (Op == "+" && a is BslStringValue sa && b is BslStringValue sb)
                return QV.Str((string)sa + (string)sb);
            if (a is not BslNumericValue na || b is not BslNumericValue nb)
                throw new QueryException($"Неверные параметры \"{Op}\": операция над типами {QV.TypeName(a)} и {QV.TypeName(b)}", Pos, Len);
            decimal x = (decimal)na, y = (decimal)nb;
            try
            {
                switch (Op)
                {
                    case "+": return QV.Num(x + y);
                    case "-": return QV.Num(x - y);
                    case "*": return QV.Num(x * y);
                    default:
                        // Файловый вариант платформы при делении на ноль ошибку не выдаёт: результат NULL
                        if (y == 0) return QV.Null;
                        return QV.Num(x / y);
                }
            }
            catch (OverflowException)
            {
                throw new QueryException("Переполнение при вычислении арифметической операции", Pos, Len);
            }
        }
    }

    internal sealed class CmpExpr : Expr
    {
        public string Op;
        public Expr L, R;
        public override IEnumerable<Expr> Children() => new[] { L, R };
        public override string Key() => $"C{Op}({L.Key()},{R.Key()})";

        public override BslValue Eval(EvalCtx c) => Compare(Op, L.Eval(c), R.Eval(c));

        public static BslValue Compare(string op, BslValue a, BslValue b)
        {
            if (QV.IsNull(a) || QV.IsNull(b)) return QV.Null;
            switch (op)
            {
                case "=": return QV.Bool(QV.KeyEquals(a, b));
                case "<>": return QV.Bool(!QV.KeyEquals(a, b));
            }
            int r = QV.Compare(a, b);
            return op switch
            {
                "<" => QV.Bool(r < 0),
                ">" => QV.Bool(r > 0),
                "<=" => QV.Bool(r <= 0),
                _ => QV.Bool(r >= 0),
            };
        }
    }

    internal sealed class AndExpr : Expr
    {
        public Expr L, R;
        public override IEnumerable<Expr> Children() => new[] { L, R };
        public override string Key() => $"AND({L.Key()},{R.Key()})";

        public override BslValue Eval(EvalCtx c)
        {
            var a = Logic.ToBool(L.Eval(c), L);
            if (a == false) return QV.False;
            var b = Logic.ToBool(R.Eval(c), R);
            if (b == false) return QV.False;
            if (a == null || b == null) return QV.Null;
            return QV.True;
        }
    }

    internal sealed class OrExpr : Expr
    {
        public Expr L, R;
        public override IEnumerable<Expr> Children() => new[] { L, R };
        public override string Key() => $"OR({L.Key()},{R.Key()})";

        public override BslValue Eval(EvalCtx c)
        {
            var a = Logic.ToBool(L.Eval(c), L);
            if (a == true) return QV.True;
            var b = Logic.ToBool(R.Eval(c), R);
            if (b == true) return QV.True;
            if (a == null || b == null) return QV.Null;
            return QV.False;
        }
    }

    internal sealed class NotExpr : Expr
    {
        public Expr E;
        public override IEnumerable<Expr> Children() => new[] { E };
        public override string Key() => $"NOT({E.Key()})";

        public override BslValue Eval(EvalCtx c)
        {
            var a = Logic.ToBool(E.Eval(c), E);
            return a == null ? QV.Null : QV.Bool(!a.Value);
        }
    }

    internal static class Logic
    {
        /// <summary>Булево → true/false, NULL → null; иначе ошибка «ожидается логическое выражение».</summary>
        public static bool? ToBool(BslValue v, Expr at)
        {
            if (v is BslBooleanValue b) return (bool)b;
            if (QV.IsNull(v)) return null;
            throw new QueryException($"Ожидается логическое выражение, получено значение типа {QV.TypeName(v)}", at.Pos, at.Len);
        }

        public static BslValue Negate(BslValue v, bool negate) =>
            !negate || QV.IsNull(v) ? v : QV.Bool(!QV.IsTrue(v));
    }

    internal sealed class IsNullExpr : Expr
    {
        public Expr E;
        public bool Negate;
        public override IEnumerable<Expr> Children() => new[] { E };
        public override string Key() => $"ISNULL{Negate}({E.Key()})";
        public override BslValue Eval(EvalCtx c) => QV.Bool(QV.IsNull(E.Eval(c)) != Negate);
    }

    internal sealed class BetweenExpr : Expr
    {
        public Expr E, Lo, Hi;
        public bool Negate;
        public override IEnumerable<Expr> Children() => new[] { E, Lo, Hi };
        public override string Key() => $"BETWEEN{Negate}({E.Key()},{Lo.Key()},{Hi.Key()})";

        public override BslValue Eval(EvalCtx c)
        {
            var v = E.Eval(c);
            var ge = CmpExpr.Compare(">=", v, Lo.Eval(c));
            var le = CmpExpr.Compare("<=", v, Hi.Eval(c));
            BslValue r;
            if (ge == QV.False || le == QV.False) r = QV.False;
            else if (QV.IsNull(ge) || QV.IsNull(le)) r = QV.Null;
            else r = QV.True;
            return Logic.Negate(r, Negate);
        }
    }

    internal sealed class InExpr : Expr
    {
        public Expr E;
        public List<Expr> Items;     // В (список)
        public QueryNode Sub;        // В (ВЫБРАТЬ …)
        public bool Negate;
        public override IEnumerable<Expr> Children() => Items != null ? new[] { E }.Concat(Items) : new[] { E };
        public override string Key() => $"IN{Negate}({E.Key()};{(Items != null ? Keys(Items) : "SUB" + Pos)})";

        public override BslValue Eval(EvalCtx c)
        {
            var v = E.Eval(c);
            if (QV.IsNull(v)) return QV.Null;
            bool sawNull = false;
            if (Sub != null)
            {
                var set = c.Run.SubquerySet(this, c);
                if (set.Contains(new[] { v })) return QV.Bool(!Negate);
                return QV.Bool(Negate);
            }
            foreach (var item in Items)
            {
                foreach (var x in ItemValues(item, c))
                {
                    if (QV.IsNull(x)) { sawNull = true; continue; }
                    if (QV.KeyEquals(v, x)) return QV.Bool(!Negate);
                }
            }
            if (sawNull) return QV.Null;
            return QV.Bool(Negate);
        }

        private static IEnumerable<BslValue> ItemValues(Expr item, EvalCtx c)
        {
            if (item is ParamExpr p) return c.Run.ListParam(p.Name, p.Pos, p.Len);
            return new[] { item.Eval(c) };
        }
    }

    internal sealed class LikeExpr : Expr
    {
        public Expr E, Pattern, Escape;
        public bool Negate;
        public override IEnumerable<Expr> Children() => Escape != null ? new[] { E, Pattern, Escape } : new[] { E, Pattern };
        public override string Key() => $"LIKE{Negate}({E.Key()},{Pattern.Key()},{Escape?.Key()})";

        public override BslValue Eval(EvalCtx c)
        {
            var v = E.Eval(c);
            var p = Pattern.Eval(c);
            if (QV.IsNull(v) || QV.IsNull(p)) return QV.Null;
            if (v is not BslStringValue sv)
                throw new QueryException($"Неверные параметры \"ПОДОБНО\": слева значение типа {QV.TypeName(v)}, ожидается Строка", Pos, Len);
            if (p is not BslStringValue sp)
                throw new QueryException("Неверные параметры \"ПОДОБНО\": шаблон должен быть строкой", Pattern.Pos, Pattern.Len);
            char? esc = null;
            if (Escape != null)
            {
                var e = Escape.Eval(c);
                if (e is not BslStringValue se || ((string)se).Length != 1)
                    throw new QueryException("СПЕЦСИМВОЛ должен быть строкой из одного символа", Escape.Pos, Escape.Len);
                esc = ((string)se)[0];
            }
            var re = c.Run.LikeRegex((string)sp, esc, Pattern);
            var ok = re.IsMatch(QV.TrimEndSpaces((string)sv));
            return QV.Bool(ok != Negate);
        }

        /// <summary>Шаблон ПОДОБНО → регулярное выражение: % — любая последовательность, _ — один символ, [..] и [^..].</summary>
        public static Regex BuildRegex(string pattern, char? escape, Expr at)
        {
            var sb = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char ch = pattern[i];
                if (escape.HasValue && ch == escape.Value)
                {
                    if (i + 1 >= pattern.Length)
                        throw new QueryException("Шаблон ПОДОБНО заканчивается спецсимволом", at.Pos, at.Len);
                    sb.Append(Regex.Escape(pattern[++i].ToString()));
                    continue;
                }
                switch (ch)
                {
                    case '%': sb.Append(".*"); break;
                    case '_': sb.Append('.'); break;
                    case '[':
                        int close = pattern.IndexOf(']', i + 1);
                        if (close < 0) { sb.Append(@"\["); break; }
                        var body = pattern.Substring(i + 1, close - i - 1);
                        var cls = new StringBuilder("[");
                        int k = 0;
                        if (body.StartsWith("^")) { cls.Append('^'); k = 1; }
                        for (; k < body.Length; k++)
                        {
                            char b = body[k];
                            if (b == '-' && k > 0 && k < body.Length - 1) cls.Append('-');
                            else if (b == '\\' || b == ']' || b == '[' || b == '^' || b == '-') cls.Append('\\').Append(b);
                            else cls.Append(b);
                        }
                        cls.Append(']');
                        sb.Append(cls);
                        i = close;
                        break;
                    default: sb.Append(Regex.Escape(ch.ToString())); break;
                }
            }
            sb.Append('$');
            try
            {
                return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
            }
            catch (ArgumentException)
            {
                throw new QueryException("Неверный шаблон ПОДОБНО", at.Pos, at.Len);
            }
        }
    }

    internal sealed class CaseExpr : Expr
    {
        public List<(Expr When, Expr Then)> Branches = new();
        public Expr Else;
        public override IEnumerable<Expr> Children()
        {
            foreach (var (w, t) in Branches) { yield return w; yield return t; }
            if (Else != null) yield return Else;
        }
        public override string Key() => $"CASE({string.Join(";", Branches.Select(b => b.When.Key() + "=>" + b.Then.Key()))};{Else?.Key()})";

        public override BslValue Eval(EvalCtx c)
        {
            foreach (var (w, t) in Branches)
                if (Logic.ToBool(w.Eval(c), w) == true) return t.Eval(c);
            return Else != null ? Else.Eval(c) : QV.Null;
        }
    }

    /// <summary>Имя периода в НАЧАЛОПЕРИОДА, ДОБАВИТЬКДАТЕ, РАЗНОСТЬДАТ: МЕСЯЦ, ДЕНЬ…</summary>
    internal sealed class PeriodExpr : Expr
    {
        public string Period;   // канонический: SECOND, MINUTE, HOUR, DAY, WEEK, MONTH, QUARTER, YEAR, TENDAYS, HALFYEAR
        public override BslValue Eval(EvalCtx c) => QV.Str(Period);
        public override string Key() => "PER:" + Period;
    }

    internal sealed class TypeExpr : Expr
    {
        public BslTypeValue Type;
        public override BslValue Eval(EvalCtx c) => Type;
        public override string Key() => "T:" + Type.TypeValue.Name;
    }

    internal sealed class FuncExpr : Expr
    {
        public string Name;        // каноническое английское имя
        public string Display;     // как написано в тексте
        public List<Expr> Args = new();
        public override IEnumerable<Expr> Children() => Args;
        public override string Key() => $"FN:{Name}({Keys(Args)})";
        public override BslValue Eval(EvalCtx c) => QueryFunctions.Call(this, c);
    }

    internal sealed class CastExpr : Expr
    {
        public Expr E;
        public string TypeName;   // NUMBER, STRING, DATE, BOOLEAN
        public int? Length, Precision;
        public override IEnumerable<Expr> Children() => new[] { E };
        public override string Key() => $"CAST:{TypeName}:{Length}:{Precision}({E.Key()})";
        public override BslValue Eval(EvalCtx c) => QueryFunctions.Cast(this, E.Eval(c));
    }

    internal sealed class AggExpr : Expr
    {
        public string Name;      // SUM, COUNT, MIN, MAX, AVG
        public string Display;
        public bool Distinct;
        public Expr Arg;         // null для КОЛИЧЕСТВО(*)
        public override IEnumerable<Expr> Children() => Arg != null ? new[] { Arg } : Array.Empty<Expr>();
        public override string Key() => $"AGG:{Name}:{Distinct}({Arg?.Key() ?? "*"})";

        public override BslValue Eval(EvalCtx c)
        {
            var rows = c.Group;
            if (rows == null)
                throw new QueryException($"Агрегатная функция {Display} здесь недопустима", Pos, Len);
            if (Arg == null) return QV.Num(rows.Count);
            var ctx = c.Child(null);
            var values = new List<BslValue>(rows.Count);
            foreach (var r in rows)
            {
                ctx.Row = r;
                var v = Arg.Eval(ctx);
                if (!QV.IsNull(v)) values.Add(v);
            }
            switch (Name)
            {
                case "COUNT":
                    if (!Distinct) return QV.Num(values.Count);
                    return QV.Num(new HashSet<BslValue[]>(values.Select(v => new[] { v }), RowKeyComparer.Instance).Count);
                case "MIN":
                case "MAX":
                    if (values.Count == 0) return QV.Null;
                    var best = values[0];
                    foreach (var v in values)
                    {
                        int cmp = QV.Compare(v, best);
                        if (Name == "MIN" ? cmp < 0 : cmp > 0) best = v;
                    }
                    return best;
                default:
                    if (values.Count == 0) return QV.Null;
                    decimal sum = 0;
                    foreach (var v in values)
                    {
                        if (v is not BslNumericValue n)
                            throw new QueryException($"Неверные параметры \"{Display}\": значение типа {QV.TypeName(v)}, ожидается Число", Pos, Len);
                        try { sum += (decimal)n; }
                        catch (OverflowException) { throw new QueryException("Переполнение при вычислении " + Display, Pos, Len); }
                    }
                    return QV.Num(Name == "SUM" ? sum : sum / values.Count);
            }
        }
    }

    // ---------- Структура запроса ----------

    internal sealed class SelectField
    {
        public Expr Expr;
        public bool IsStar;
        public string StarSource;   // Т.* — имя источника; * — null
        public string Alias;
        public int Pos;
        public int Len;
    }

    internal enum SourceKind { Param, Table, Subquery }

    internal sealed class SourceRef
    {
        public SourceKind Kind;
        public string Name;           // имя параметра / таблицы (с точками) / null
        public QueryNode Sub;
        public List<Expr> Args;       // виртуальная таблица: Остатки(&Дата)
        public string Alias;
        public int Pos;
        public int Len;
    }

    internal enum JoinKind { Inner, Left, Right, Full }

    internal sealed class JoinRef
    {
        public JoinKind Kind;
        public SourceRef Source;
        public Expr On;
        public int Pos;
    }

    internal sealed class FromItem
    {
        public SourceRef Source;
        public List<JoinRef> Joins = new();
    }

    internal sealed class SelectCore
    {
        public bool Distinct;
        public int? Top;
        public List<SelectField> Fields = new();
        public List<FromItem> From = new();
        public Expr Where;
        public List<Expr> GroupBy;
        public Expr Having;
        public int Pos;
    }

    internal sealed class OrderItem
    {
        public Expr Expr;
        public bool Desc;
    }

    internal sealed class QueryNode
    {
        public List<SelectCore> Cores = new();
        public List<bool> UnionAll = new();   // UnionAll[i] — между частями i и i+1
        public List<OrderItem> OrderBy = new();
        public string Into;
        public int IntoPos;
        public bool Correlated;
        public int Pos;
    }

    internal abstract class Statement
    {
        public int Pos;
    }

    internal sealed class SelectStatement : Statement
    {
        public QueryNode Query;
    }

    internal sealed class DropStatement : Statement
    {
        public string Name;
        public int Len;
    }
}
