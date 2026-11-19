// Язык запросов: синтаксический разбор пакета (рекурсивный спуск).
// Ошибки — QueryException с позицией лексемы, тексты как в платформе: Синтаксическая ошибка "ИЗ".
using System;
using System.Collections.Generic;
using System.Linq;
using OneScript.Types;
using OneScript.Values;

namespace OneScriptWasm.Query
{
    internal sealed class QueryParser
    {
        // Функции: русское и английское имя → каноническое, допустимое число аргументов.
        private static readonly Dictionary<string, (string Name, int Min, int Max)> Functions = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
        {
            ["СУММА"] = "SUM", ["SUM"] = "SUM",
            ["КОЛИЧЕСТВО"] = "COUNT", ["COUNT"] = "COUNT",
            ["МИНИМУМ"] = "MIN", ["MIN"] = "MIN",
            ["МАКСИМУМ"] = "MAX", ["MAX"] = "MAX",
            ["СРЕДНЕЕ"] = "AVG", ["AVG"] = "AVG",
        };
        private static readonly Dictionary<string, string> Periods = new(StringComparer.OrdinalIgnoreCase)
        {
            ["СЕКУНДА"] = "SECOND", ["SECOND"] = "SECOND",
            ["МИНУТА"] = "MINUTE", ["MINUTE"] = "MINUTE",
            ["ЧАС"] = "HOUR", ["HOUR"] = "HOUR",
            ["ДЕНЬ"] = "DAY", ["DAY"] = "DAY",
            ["НЕДЕЛЯ"] = "WEEK", ["WEEK"] = "WEEK",
            ["МЕСЯЦ"] = "MONTH", ["MONTH"] = "MONTH",
            ["КВАРТАЛ"] = "QUARTER", ["QUARTER"] = "QUARTER",
            ["ГОД"] = "YEAR", ["YEAR"] = "YEAR",
            ["ДЕКАДА"] = "TENDAYS", ["TENDAYS"] = "TENDAYS",
            ["ПОЛУГОДИЕ"] = "HALFYEAR", ["HALFYEAR"] = "HALFYEAR",
        };
        private static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ЧИСЛО"] = "NUMBER", ["NUMBER"] = "NUMBER",
            ["СТРОКА"] = "STRING", ["STRING"] = "STRING",
            ["ДАТА"] = "DATE", ["DATE"] = "DATE",
            ["БУЛЕВО"] = "BOOLEAN", ["BOOLEAN"] = "BOOLEAN",
        };
        // Номер аргумента-периода у функций дат
        private static readonly Dictionary<string, int> PeriodArg = new()
        {
            ["BEGINOFPERIOD"] = 1, ["ENDOFPERIOD"] = 1, ["DATEADD"] = 1, ["DATEDIFF"] = 2,
        };

        static QueryParser()
        {
            void F(string ru, string en, int min, int max)
            {
                Functions[ru] = (en, min, max);
                Functions[en] = (en, min, max);
            }
            F("ПОДСТРОКА", "SUBSTRING", 3, 3);
            F("ДЛИНАСТРОКИ", "STRINGLENGTH", 1, 1);
            F("ВРЕГ", "UPPER", 1, 1);
            F("НРЕГ", "LOWER", 1, 1);
            F("СОКРЛ", "TRIML", 1, 1);
            F("СОКРП", "TRIMR", 1, 1);
            F("СОКРЛП", "TRIMALL", 1, 1);
            F("ЛЕВ", "LEFT", 2, 2);
            F("ПРАВ", "RIGHT", 2, 2);
            F("СТРНАЙТИ", "STRFIND", 2, 2);
            F("СТРЗАМЕНИТЬ", "STRREPLACE", 3, 3);
            F("СТРОКА", "STRING", 1, 1);
            F("ПРЕДСТАВЛЕНИЕ", "PRESENTATION", 1, 1);
            F("ОКР", "ROUND", 1, 2);
            F("ЦЕЛ", "INT", 1, 1);
            foreach (var m in new[] { "ACOS", "ASIN", "ATAN", "COS", "SIN", "TAN", "EXP", "LOG", "LOG10", "SQRT" })
                F(m, m, 1, 1);
            F("POW", "POW", 2, 2);
            F("ГОД", "YEAR", 1, 1);
            F("КВАРТАЛ", "QUARTER", 1, 1);
            F("МЕСЯЦ", "MONTH", 1, 1);
            F("ДЕНЬГОДА", "DAYOFYEAR", 1, 1);
            F("ДЕНЬ", "DAY", 1, 1);
            F("НЕДЕЛЯ", "WEEK", 1, 1);
            F("ДЕНЬНЕДЕЛИ", "WEEKDAY", 1, 1);
            F("ЧАС", "HOUR", 1, 1);
            F("МИНУТА", "MINUTE", 1, 1);
            F("СЕКУНДА", "SECOND", 1, 1);
            F("НАЧАЛОПЕРИОДА", "BEGINOFPERIOD", 2, 2);
            F("КОНЕЦПЕРИОДА", "ENDOFPERIOD", 2, 2);
            F("ДОБАВИТЬКДАТЕ", "DATEADD", 3, 3);
            F("РАЗНОСТЬДАТ", "DATEDIFF", 3, 3);
            F("ДАТАВРЕМЯ", "DATETIME", 3, 6);
            F("ЕСТЬNULL", "ISNULL", 2, 2);
            F("ТИПЗНАЧЕНИЯ", "VALUETYPE", 1, 1);
        }

        // Частые ошибки: имена функций встроенного языка, которых нет в языке запросов
        private static readonly Dictionary<string, string> Hints = new(StringComparer.OrdinalIgnoreCase)
        {
            ["СТРДЛИНА"] = "в языке запросов длина строки — ДЛИНАСТРОКИ()",
            ["ПУСТАЯСТРОКА"] = "сравните строку с \"\"",
            ["ЗНАЧЕНИЕЗАПОЛНЕНО"] = "в языке запросов такой функции нет",
            ["ФОРМАТ"] = "в языке запросов нет функции ФОРМАТ, используйте СТРОКА() или ВЫРАЗИТЬ()",
            ["ТЕКУЩАЯДАТА"] = "передайте дату параметром: &ТекущаяДата",
            ["ДАТА"] = "литерал даты в запросе — ДАТАВРЕМЯ(2024, 1, 31)",
            ["ЧИСЛО"] = "приведение типа в запросе — ВЫРАЗИТЬ(… КАК Число(15, 2))",
        };

        private readonly List<Token> _t;
        private int _i;

        private QueryParser(List<Token> tokens) { _t = tokens; }

        public static List<Statement> ParseBatch(string text)
        {
            var p = new QueryParser(QueryLexer.Tokenize(text ?? ""));
            return p.Batch();
        }

        public static bool IsFunctionName(string name) => Functions.ContainsKey(name) || Aggregates.ContainsKey(name);

        // ---------- Лексемы ----------

        private Token Cur => _t[_i];
        private Token Peek(int k = 1) => _t[Math.Min(_i + k, _t.Count - 1)];
        private Token Next() => _t[_i++];

        private QueryException Unexpected(Token t = null)
        {
            t ??= Cur;
            if (t.Kind == TokKind.End) return new QueryException("Синтаксическая ошибка: неожиданный конец текста запроса", t.Pos, 0);
            return new QueryException($"Синтаксическая ошибка \"{t.Text}\"", t.Pos, t.Length);
        }

        private bool AcceptKw(string kw)
        {
            if (!Cur.Is(kw)) return false;
            _i++;
            return true;
        }

        private Token ExpectKw(string kw, string what)
        {
            if (Cur.Is(kw)) return Next();
            throw new QueryException($"Ожидается {what}, найдено \"{Cur}\"", Cur.Pos, Cur.Length);
        }

        private bool AcceptOp(string op)
        {
            if (!Cur.IsOp(op)) return false;
            _i++;
            return true;
        }

        private Token ExpectOp(string op)
        {
            if (Cur.IsOp(op)) return Next();
            throw new QueryException($"Ожидается символ \"{op}\", найдено \"{Cur}\"", Cur.Pos, Cur.Length);
        }

        /// <summary>Псевдоним после КАК: допускается и служебное слово (Т.Сумма КАК В), после КАК оно однозначно.</summary>
        private Token ExpectAlias(string what)
        {
            if (Cur.Kind == TokKind.Ident || Cur.Kind == TokKind.Keyword && Cur.Kw != "SELECT") return Next();
            throw new QueryException($"Ожидается {what}, найдено \"{Cur}\"", Cur.Pos, Cur.Length);
        }

        private Token ExpectIdent(string what)
        {
            if (Cur.Kind == TokKind.Ident) return Next();
            throw new QueryException($"Ожидается {what}, найдено \"{Cur}\"", Cur.Pos, Cur.Length);
        }

        // ---------- Пакет и операторы ----------

        private List<Statement> Batch()
        {
            var list = new List<Statement>();
            while (true)
            {
                while (AcceptOp(";")) { }
                if (Cur.Kind == TokKind.End) break;
                if (Cur.Is("SELECT"))
                {
                    var pos = Cur.Pos;
                    var q = Query(allowInto: true, nested: false);
                    list.Add(new SelectStatement { Pos = pos, Query = q });
                }
                else if (Cur.Is("DROP"))
                {
                    var pos = Next().Pos;
                    var name = ExpectIdent("имя временной таблицы");
                    list.Add(new DropStatement { Pos = name.Pos, Len = name.Length, Name = name.Value });
                }
                else
                {
                    throw new QueryException($"Ожидается выражение \"ВЫБРАТЬ\", найдено \"{Cur}\"", Cur.Pos, Cur.Length);
                }
                if (Cur.Kind == TokKind.End) break;
                if (!Cur.IsOp(";")) throw Unexpected();
            }
            if (list.Count == 0)
                throw new QueryException("Текст запроса пуст: ожидается ВЫБРАТЬ", 0, 0);
            return list;
        }

        /// <summary>ВЫБРАТЬ … [ОБЪЕДИНИТЬ [ВСЕ] ВЫБРАТЬ …] [УПОРЯДОЧИТЬ ПО …] [АВТОУПОРЯДОЧИВАНИЕ] [ИНДЕКСИРОВАТЬ ПО …]</summary>
        private QueryNode Query(bool allowInto, bool nested)
        {
            var q = new QueryNode { Pos = Cur.Pos };
            q.Cores.Add(Core(q, allowInto));
            while (Cur.Is("UNION"))
            {
                Next();
                bool all = Cur.Kind == TokKind.Ident && (Cur.Value.Equals("ВСЕ", StringComparison.OrdinalIgnoreCase) || Cur.Value.Equals("ALL", StringComparison.OrdinalIgnoreCase));
                if (all) Next();
                q.UnionAll.Add(all);
                if (!Cur.Is("SELECT")) throw new QueryException($"Ожидается ВЫБРАТЬ после ОБЪЕДИНИТЬ, найдено \"{Cur}\"", Cur.Pos, Cur.Length);
                q.Cores.Add(Core(q, false));
            }
            if (Cur.Is("ORDER"))
            {
                Next();
                ExpectKw("BY", "ПО после УПОРЯДОЧИТЬ");
                do
                {
                    var item = new OrderItem { Expr = Expr() };
                    if (AcceptKw("DESC")) item.Desc = true;
                    else AcceptKw("ASC");
                    q.OrderBy.Add(item);
                } while (AcceptOp(","));
            }
            AcceptKw("AUTOORDER");
            if (Cur.Is("TOTALS"))
                throw new QueryException("Конструкция ИТОГИ не поддерживается в песочнице", Cur.Pos, Cur.Length);
            if (!nested && Cur.Is("INDEX"))
            {
                var at = Next();
                ExpectKw("BY", "ПО после ИНДЕКСИРОВАТЬ");
                if (q.Into == null)
                    throw new QueryException("ИНДЕКСИРОВАТЬ ПО допустимо только в запросе с ПОМЕСТИТЬ", at.Pos, at.Length);
                do { Expr(); } while (AcceptOp(","));
            }
            return q;
        }

        private SelectCore Core(QueryNode q, bool allowInto)
        {
            var core = new SelectCore { Pos = ExpectKw("SELECT", "ВЫБРАТЬ").Pos };
            // РАЗРЕШЕННЫЕ, РАЗЛИЧНЫЕ, ПЕРВЫЕ N
            while (true)
            {
                if (AcceptKw("ALLOWED")) continue;
                if (AcceptKw("DISTINCT")) { core.Distinct = true; continue; }
                if (Cur.Is("TOP"))
                {
                    Next();
                    if (Cur.Kind != TokKind.Number || Cur.Number != Math.Floor(Cur.Number))
                        throw new QueryException($"После ПЕРВЫЕ ожидается целое число, найдено \"{Cur}\"", Cur.Pos, Cur.Length);
                    core.Top = (int)Math.Min(Next().Number, int.MaxValue);
                    continue;
                }
                break;
            }
            do core.Fields.Add(Field()); while (AcceptOp(","));

            if (Cur.Is("INTO"))
            {
                var at = Next();
                if (!allowInto)
                    throw new QueryException("ПОМЕСТИТЬ допускается только в первом запросе пакета или объединения", at.Pos, at.Length);
                var name = ExpectIdent("имя временной таблицы");
                q.Into = name.Value;
                q.IntoPos = name.Pos;
            }
            if (AcceptKw("FROM"))
            {
                do core.From.Add(FromItem()); while (AcceptOp(","));
            }
            if (AcceptKw("WHERE")) core.Where = Expr();
            if (Cur.Is("GROUP"))
            {
                Next();
                ExpectKw("BY", "ПО после СГРУППИРОВАТЬ");
                if (Cur.Kind == TokKind.Ident && Cur.Value.Equals("ГРУППИРУЮЩИМ", StringComparison.OrdinalIgnoreCase))
                    throw new QueryException("Конструкция ГРУППИРУЮЩИМ НАБОРАМ не поддерживается в песочнице", Cur.Pos, Cur.Length);
                core.GroupBy = new List<Expr>();
                do core.GroupBy.Add(Expr()); while (AcceptOp(","));
            }
            if (AcceptKw("HAVING")) core.Having = Expr();
            if (Cur.Is("FOR"))
            {
                Next();
                if (!(Cur.Kind == TokKind.Ident && (Cur.Value.Equals("ИЗМЕНЕНИЯ", StringComparison.OrdinalIgnoreCase) || Cur.Value.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))))
                    throw new QueryException($"Ожидается ИЗМЕНЕНИЯ после ДЛЯ, найдено \"{Cur}\"", Cur.Pos, Cur.Length);
                Next();
                // Список таблиц для блокировки: в песочнице блокировок нет
                while (Cur.Kind == TokKind.Ident)
                {
                    Next();
                    while (AcceptOp(".")) ExpectIdent("имя таблицы");
                    if (!AcceptOp(",")) break;
                }
            }
            return core;
        }

        private SelectField Field()
        {
            var start = Cur;
            if (Cur.IsOp("*"))
            {
                Next();
                return new SelectField { IsStar = true, Pos = start.Pos, Len = 1 };
            }
            if (Cur.Kind == TokKind.Ident && Peek().IsOp(".") && Peek(2).IsOp("*"))
            {
                var src = Next();
                Next(); Next();
                return new SelectField { IsStar = true, StarSource = src.Value, Pos = src.Pos, Len = Peek(-1).Pos + 1 - src.Pos };
            }
            var e = Expr();
            var f = new SelectField { Expr = e, Pos = start.Pos, Len = Math.Max(1, _t[_i - 1].Pos + _t[_i - 1].Length - start.Pos) };
            if (AcceptKw("AS"))
            {
                var alias = ExpectAlias("псевдоним поля");
                f.Alias = alias.Value;
            }
            else if (Cur.Kind == TokKind.Ident)
            {
                f.Alias = Next().Value;
            }
            return f;
        }

        private FromItem FromItem()
        {
            var item = new FromItem { Source = Source() };
            while (true)
            {
                var at = Cur;
                JoinKind kind;
                if (Cur.Is("JOIN")) kind = JoinKind.Inner;
                else if (Cur.Is("INNER")) { Next(); kind = JoinKind.Inner; }
                else if (Cur.Is("LEFT") && !Peek().IsOp("(")) { Next(); kind = JoinKind.Left; }
                else if (Cur.Is("RIGHT") && !Peek().IsOp("(")) { Next(); kind = JoinKind.Right; }
                else if (Cur.Is("FULL")) { Next(); kind = JoinKind.Full; }
                else break;
                AcceptKw("OUTER");
                ExpectKw("JOIN", "СОЕДИНЕНИЕ");
                var src = Source();
                if (!Cur.Is("BY"))
                    throw new QueryException($"Ожидается ПО (условие соединения), найдено \"{Cur}\"", Cur.Pos, Cur.Length);
                Next();
                item.Joins.Add(new JoinRef { Kind = kind, Source = src, On = Expr(), Pos = at.Pos });
            }
            return item;
        }

        private SourceRef Source()
        {
            var start = Cur;
            SourceRef s;
            if (Cur.Kind == TokKind.Param)
            {
                var p = Next();
                s = new SourceRef { Kind = SourceKind.Param, Name = p.Value, Pos = p.Pos, Len = p.Length, Alias = p.Value };
            }
            else if (Cur.IsOp("("))
            {
                Next();
                if (!Cur.Is("SELECT")) throw new QueryException($"Ожидается вложенный запрос ВЫБРАТЬ, найдено \"{Cur}\"", Cur.Pos, Cur.Length);
                var sub = Query(allowInto: false, nested: true);
                var close = ExpectOp(")");
                s = new SourceRef { Kind = SourceKind.Subquery, Sub = sub, Pos = start.Pos, Len = close.Pos + 1 - start.Pos };
            }
            else if (Cur.Kind == TokKind.Ident)
            {
                var first = Next();
                var parts = new List<string> { first.Value };
                int end = first.Pos + first.Length;
                while (Cur.IsOp(".") && Peek().Kind == TokKind.Ident)
                {
                    Next();
                    var p = Next();
                    parts.Add(p.Value);
                    end = p.Pos + p.Length;
                }
                s = new SourceRef { Kind = SourceKind.Table, Name = string.Join(".", parts), Pos = first.Pos, Len = end - first.Pos, Alias = parts[^1] };
                if (Cur.IsOp("("))
                {
                    // Виртуальная таблица регистра: Остатки(&Период, …)
                    Next();
                    s.Args = new List<Expr>();
                    if (!Cur.IsOp(")"))
                    {
                        do s.Args.Add(Cur.IsOp(",") ? null : Expr()); while (AcceptOp(","));
                    }
                    var close = ExpectOp(")");
                    s.Len = close.Pos + 1 - s.Pos;
                }
            }
            else
            {
                throw new QueryException($"Ожидается источник данных (таблица, &Параметр или вложенный запрос), найдено \"{Cur}\"", Cur.Pos, Cur.Length);
            }

            if (AcceptKw("AS"))
                s.Alias = ExpectAlias("псевдоним источника").Value;
            else if (Cur.Kind == TokKind.Ident)
                s.Alias = Next().Value;
            else if (s.Kind == SourceKind.Subquery)
                throw new QueryException("Ожидается псевдоним вложенного запроса: (ВЫБРАТЬ …) КАК Имя", Cur.Pos, Cur.Length);
            return s;
        }

        // ---------- Выражения ----------

        public Expr Expr() => Or();

        private Expr Or()
        {
            var l = And();
            while (Cur.Is("OR"))
            {
                var op = Next();
                var r = And();
                l = new OrExpr { L = l, R = r, Pos = l.Pos, Len = End(r) - l.Pos };
            }
            return l;
        }

        private Expr And()
        {
            var l = Not();
            while (Cur.Is("AND"))
            {
                Next();
                var r = Not();
                l = new AndExpr { L = l, R = r, Pos = l.Pos, Len = End(r) - l.Pos };
            }
            return l;
        }

        private Expr Not()
        {
            if (Cur.Is("NOT"))
            {
                var at = Next();
                var e = Not();
                return new NotExpr { E = e, Pos = at.Pos, Len = End(e) - at.Pos };
            }
            return Predicate();
        }

        private static int End(Expr e) => e.Pos + e.Len;

        private Expr Predicate()
        {
            var l = Additive();
            var c = Cur;
            if (c.Kind == TokKind.Op && (c.Text == "=" || c.Text == "<>" || c.Text == "<" || c.Text == ">" || c.Text == "<=" || c.Text == ">="))
            {
                Next();
                var r = Additive();
                return new CmpExpr { Op = c.Text, L = l, R = r, Pos = l.Pos, Len = End(r) - l.Pos };
            }
            if (c.Is("IS"))
            {
                Next();
                bool neg = AcceptKw("NOT");
                var nul = ExpectKw("NULL", "NULL после ЕСТЬ");
                return new IsNullExpr { E = l, Negate = neg, Pos = l.Pos, Len = nul.Pos + nul.Length - l.Pos };
            }
            bool negate = false;
            if (c.Is("NOT") && (Peek().Is("IN") || Peek().Is("BETWEEN") || Peek().Is("LIKE")))
            {
                Next();
                negate = true;
            }
            if (Cur.Is("IN"))
            {
                var at = Next();
                if (Cur.Kind == TokKind.Ident && (Cur.Value.Equals("ИЕРАРХИИ", StringComparison.OrdinalIgnoreCase) || Cur.Value.Equals("HIERARCHY", StringComparison.OrdinalIgnoreCase)))
                    throw new QueryException("Конструкция В ИЕРАРХИИ доступна только для справочников информационной базы", at.Pos, at.Length);
                ExpectOp("(");
                var e = new InExpr { E = l, Negate = negate, Pos = l.Pos };
                if (Cur.Is("SELECT"))
                {
                    e.Sub = Query(allowInto: false, nested: true);
                    if (e.Sub.Cores.Any(core => core.Fields.Count != 1 || core.Fields[0].IsStar))
                        throw new QueryException("Вложенный запрос в условии В должен выбирать ровно одно поле", e.Sub.Pos, 6);
                }
                else
                {
                    e.Items = new List<Expr>();
                    do e.Items.Add(Additive()); while (AcceptOp(","));
                }
                var close = ExpectOp(")");
                e.Len = close.Pos + 1 - l.Pos;
                return e;
            }
            if (Cur.Is("BETWEEN"))
            {
                Next();
                var lo = Additive();
                ExpectKw("AND", "И в условии МЕЖДУ … И …");
                var hi = Additive();
                return new BetweenExpr { E = l, Lo = lo, Hi = hi, Negate = negate, Pos = l.Pos, Len = End(hi) - l.Pos };
            }
            if (Cur.Is("LIKE"))
            {
                Next();
                var p = Additive();
                Expr esc = null;
                if (AcceptKw("ESCAPE")) esc = Additive();
                var last = esc ?? p;
                return new LikeExpr { E = l, Pattern = p, Escape = esc, Negate = negate, Pos = l.Pos, Len = End(last) - l.Pos };
            }
            if (Cur.Is("REFS"))
                throw new QueryException("Конструкция ССЫЛКА доступна только для ссылок информационной базы", Cur.Pos, Cur.Length);
            if (negate) throw Unexpected();
            return l;
        }

        private Expr Additive()
        {
            var l = Multiplicative();
            while (Cur.IsOp("+") || Cur.IsOp("-"))
            {
                var op = Next();
                var r = Multiplicative();
                l = new ArithExpr { Op = op.Text, L = l, R = r, Pos = l.Pos, Len = End(r) - l.Pos };
            }
            return l;
        }

        private Expr Multiplicative()
        {
            var l = Unary();
            while (Cur.IsOp("*") || Cur.IsOp("/"))
            {
                var op = Next();
                var r = Unary();
                l = new ArithExpr { Op = op.Text, L = l, R = r, Pos = l.Pos, Len = End(r) - l.Pos };
            }
            return l;
        }

        private Expr Unary()
        {
            if (Cur.IsOp("-") || Cur.IsOp("+"))
            {
                var op = Next();
                var e = Unary();
                // Отрицательный числовой литерал сразу сворачиваем в константу
                if (op.Text == "-" && e is LitExpr lit && lit.Value is BslNumericValue n)
                    return new LitExpr { Value = QV.Num(-(decimal)n), Pos = op.Pos, Len = End(e) - op.Pos };
                return new UnaryExpr { Op = op.Text, E = e, Pos = op.Pos, Len = End(e) - op.Pos };
            }
            return Primary();
        }

        private Expr Primary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokKind.Number:
                    Next();
                    return new LitExpr { Value = QV.Num(t.Number), Pos = t.Pos, Len = t.Length };
                case TokKind.String:
                    Next();
                    return new LitExpr { Value = QV.Str(t.Value), Pos = t.Pos, Len = t.Length };
                case TokKind.Param:
                    Next();
                    return new ParamExpr { Name = t.Value, Pos = t.Pos, Len = t.Length };
                case TokKind.Keyword:
                    switch (t.Kw)
                    {
                        case "TRUE": Next(); return new LitExpr { Value = QV.True, Pos = t.Pos, Len = t.Length };
                        case "FALSE": Next(); return new LitExpr { Value = QV.False, Pos = t.Pos, Len = t.Length };
                        case "NULL": Next(); return new LitExpr { Value = QV.Null, Pos = t.Pos, Len = t.Length };
                        case "UNDEFINED": Next(); return new LitExpr { Value = QV.Undefined, Pos = t.Pos, Len = t.Length };
                        case "CASE": return Case();
                        case "SELECT":
                            break;
                        default:
                            // Псевдоним-служебное слово (… КАК В) в начале пути к полю: В.Ид
                            if (Peek().IsOp(".") && Peek(2).Kind is TokKind.Ident or TokKind.Keyword) return FieldPath();
                            break;
                        case "LEFT":
                        case "RIGHT":
                            // LEFT(…) и RIGHT(…) по-английски — функции ЛЕВ и ПРАВ
                            if (Peek().IsOp("(")) return Function();
                            break;
                    }
                    throw Unexpected();
                case TokKind.Op when t.Text == "(":
                    Next();
                    if (Cur.Is("SELECT"))
                        throw new QueryException("Вложенный запрос в списке выборки и выражениях не поддерживается языком запросов 1С: используйте соединение или В (ВЫБРАТЬ …)", Cur.Pos, Cur.Length);
                    var inner = Expr();
                    var close = ExpectOp(")");
                    inner.Pos = Math.Min(inner.Pos, t.Pos);
                    inner.Len = Math.Max(End(inner), close.Pos + 1) - inner.Pos;
                    return inner;
                case TokKind.Ident:
                    if (Peek().IsOp("(")) return Function();
                    return FieldPath();
                default:
                    throw t.Kind == TokKind.End
                        ? new QueryException("Ожидается выражение, найден конец текста запроса", t.Pos, 0)
                        : Unexpected();
            }
        }

        private Expr FieldPath()
        {
            var first = Next();
            var parts = new List<string> { first.Value };
            int end = first.Pos + first.Length;
            int lastPos = first.Pos;
            while (Cur.IsOp(".") && (Peek().Kind == TokKind.Ident || Peek().Kind == TokKind.Keyword && Peek().Kw != "SELECT"))
            {
                Next();
                var p = Next();
                parts.Add(p.Value ?? p.Text);
                end = p.Pos + p.Length;
                lastPos = p.Pos;
            }
            // Позиция ошибки «Поле не найдено» — на последней части имени, как в платформе: Т.<<?>>Нет
            return new FieldExpr { Parts = parts.ToArray(), Pos = first.Pos, Len = end - first.Pos, PartPos = lastPos };
        }

        private Expr Case()
        {
            var start = Next();
            var c = new CaseExpr { Pos = start.Pos };
            if (!Cur.Is("WHEN"))
                throw new QueryException("Ожидается КОГДА: в языке запросов есть только форма ВЫБОР КОГДА <условие> ТОГДА …", Cur.Pos, Cur.Length);
            while (AcceptKw("WHEN"))
            {
                var w = Expr();
                ExpectKw("THEN", "ТОГДА");
                var th = Expr();
                c.Branches.Add((w, th));
            }
            if (AcceptKw("ELSE")) c.Else = Expr();
            var end = ExpectKw("END", "КОНЕЦ");
            c.Len = end.Pos + end.Length - start.Pos;
            return c;
        }

        private Expr Function()
        {
            var name = Next();
            var open = ExpectOp("(");
            var word = name.Value ?? name.Text;
            if (name.Kind == TokKind.Keyword) word = name.Kw == "LEFT" ? "LEFT" : "RIGHT";

            if (Aggregates.TryGetValue(word, out var agg))
            {
                var a = new AggExpr { Name = agg, Display = name.Text.ToUpperInvariant(), Pos = name.Pos };
                if (Cur.Is("DISTINCT"))
                {
                    var d = Next();
                    if (agg != "COUNT")
                        throw new QueryException($"РАЗЛИЧНЫЕ допускается только в агрегатной функции КОЛИЧЕСТВО", d.Pos, d.Length);
                    a.Distinct = true;
                }
                if (Cur.IsOp("*"))
                {
                    var star = Next();
                    if (agg != "COUNT" || a.Distinct)
                        throw new QueryException("Синтаксическая ошибка \"*\"", star.Pos, 1);
                }
                else
                {
                    a.Arg = Expr();
                }
                var close = ExpectOp(")");
                a.Len = close.Pos + 1 - name.Pos;
                return a;
            }

            var upper = word.ToUpperInvariant();
            if (upper == "ВЫРАЗИТЬ" || upper == "CAST") return Cast(name);
            if (upper == "ТИП" || upper == "TYPE")
            {
                var tn = ExpectIdent("имя типа");
                var close = ExpectOp(")");
                return new TypeExpr { Type = MakeType(tn), Pos = name.Pos, Len = close.Pos + 1 - name.Pos };
            }
            if (upper == "ЗНАЧЕНИЕ" || upper == "VALUE")
                throw new QueryException("Конструкция ЗНАЧЕНИЕ() доступна только для предопределённых данных информационной базы", name.Pos, name.Length);
            if (upper == "УНИКАЛЬНЫЙИДЕНТИФИКАТОР" || upper == "UUID")
                throw new QueryException("Функция УНИКАЛЬНЫЙИДЕНТИФИКАТОР доступна только для ссылок информационной базы", name.Pos, name.Length);

            if (!Functions.TryGetValue(word, out var fn))
            {
                var hint = Hints.TryGetValue(word, out var h) ? ": " + h : "";
                throw new QueryException($"Неизвестная функция \"{name.Text}\"{hint}", name.Pos, name.Length);
            }
            var f = new FuncExpr { Name = fn.Name, Display = name.Text.ToUpperInvariant(), Pos = name.Pos };
            PeriodArg.TryGetValue(fn.Name, out var periodIndex);
            if (!PeriodArg.ContainsKey(fn.Name)) periodIndex = -1;
            if (!Cur.IsOp(")"))
            {
                do
                {
                    if (f.Args.Count == periodIndex)
                    {
                        var pt = Cur;
                        var word2 = pt.Kind == TokKind.Ident ? pt.Value : null;
                        if (word2 == null || !Periods.TryGetValue(word2, out var period))
                            throw new QueryException($"Ожидается период (ДЕНЬ, МЕСЯЦ, ГОД…), найдено \"{pt}\"", pt.Pos, pt.Length);
                        Next();
                        f.Args.Add(new PeriodExpr { Period = period, Pos = pt.Pos, Len = pt.Length });
                    }
                    else
                    {
                        f.Args.Add(Expr());
                    }
                } while (AcceptOp(","));
            }
            var end = ExpectOp(")");
            f.Len = end.Pos + 1 - name.Pos;
            if (f.Args.Count < fn.Min || f.Args.Count > fn.Max)
                throw new QueryException($"Неверное количество параметров функции {f.Display}", name.Pos, f.Len);
            QueryFunctions.Validate(f);
            return f;
        }

        private Expr Cast(Token name)
        {
            var e = Expr();
            ExpectKw("AS", "КАК в ВЫРАЗИТЬ(… КАК Тип)");
            var tn = Cur;
            if (tn.Kind != TokKind.Ident || !TypeNames.TryGetValue(tn.Value, out var type))
            {
                if (tn.Kind == TokKind.Ident && Peek().IsOp("."))
                    throw new QueryException($"Тип \"{tn.Text}.…\" есть только в информационной базе: в песочнице ВЫРАЗИТЬ приводит к Число, Строка, Дата, Булево", tn.Pos, tn.Length);
                throw new QueryException($"Ожидается тип (Число, Строка, Дата, Булево), найдено \"{tn}\"", tn.Pos, tn.Length);
            }
            Next();
            var c = new CastExpr { E = e, TypeName = type, Pos = name.Pos };
            if (AcceptOp("("))
            {
                c.Length = IntArg();
                if (AcceptOp(",")) c.Precision = IntArg();
                ExpectOp(")");
            }
            var close = ExpectOp(")");
            c.Len = close.Pos + 1 - name.Pos;
            // Литерал заведомо другого типа — ошибка, как в платформе: ВЫРАЗИТЬ("123" КАК Число)
            if (e is LitExpr lit && !QV.IsNull(lit.Value) && !QueryFunctions.CastTypeMatches(type, lit.Value))
                throw new QueryException($"Несовместимые типы для операции ВЫРАЗИТЬ: {QV.TypeName(lit.Value)} нельзя выразить как {tn.Text}", name.Pos, c.Len);
            return c;
        }

        private int IntArg()
        {
            if (Cur.Kind != TokKind.Number || Cur.Number != Math.Floor(Cur.Number) || Cur.Number > 1000)
                throw new QueryException($"Ожидается целое число, найдено \"{Cur}\"", Cur.Pos, Cur.Length);
            return (int)Next().Number;
        }

        private static BslTypeValue MakeType(Token tn)
        {
            if (!TypeNames.TryGetValue(tn.Value, out var t))
            {
                if (tn.Value.Equals("Null", StringComparison.OrdinalIgnoreCase)) return new BslTypeValue(BasicTypes.Null);
                throw new QueryException($"Тип \"{tn.Text}\" недоступен в песочнице: можно Число, Строка, Дата, Булево", tn.Pos, tn.Length);
            }
            return new BslTypeValue(t switch
            {
                "NUMBER" => BasicTypes.Number,
                "STRING" => BasicTypes.String,
                "DATE" => BasicTypes.Date,
                _ => BasicTypes.Boolean,
            });
        }
    }
}
