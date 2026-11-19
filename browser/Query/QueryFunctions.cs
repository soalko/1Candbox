// Язык запросов: встроенные функции (строки, числа, даты, ЕСТЬNULL, ТИПЗНАЧЕНИЯ) и ВЫРАЗИТЬ.
// NULL-аргумент даёт NULL; аргумент неподходящего типа — ошибка «Неверные параметры "ФУНКЦИЯ"».
using System;
using System.Globalization;
using System.Text;
using OneScript.Values;

namespace OneScriptWasm.Query
{
    internal static class QueryFunctions
    {
        private static readonly CultureInfo Ru = new("ru-RU");

        /// <summary>Проверки при разборе: допустимые периоды у функций дат.</summary>
        public static void Validate(FuncExpr f)
        {
            string period = null;
            Expr at = null;
            foreach (var a in f.Args)
                if (a is PeriodExpr p) { period = p.Period; at = p; }
            if (period == null) return;
            bool ok = f.Name switch
            {
                "BEGINOFPERIOD" or "ENDOFPERIOD" => period != "SECOND",
                "DATEDIFF" => period is "SECOND" or "MINUTE" or "HOUR" or "DAY" or "MONTH" or "QUARTER" or "YEAR",
                _ => true,
            };
            if (!ok) throw new QueryException($"Неверные параметры \"{f.Display}\": период недопустим для этой функции", at.Pos, at.Len);
        }

        private static QueryException Bad(FuncExpr f, string why = null) =>
            new($"Неверные параметры \"{f.Display}\"" + (why != null ? ": " + why : ""), f.Pos, f.Len);

        private static string Str(FuncExpr f, BslValue v) =>
            v is BslStringValue s ? (string)s : throw Bad(f, $"ожидается Строка, получено значение типа {QV.TypeName(v)}");

        private static decimal Num(FuncExpr f, BslValue v) =>
            v is BslNumericValue n ? (decimal)n : throw Bad(f, $"ожидается Число, получено значение типа {QV.TypeName(v)}");

        private static DateTime Date(FuncExpr f, BslValue v) =>
            v is BslDateValue d ? (DateTime)d : throw Bad(f, $"ожидается Дата, получено значение типа {QV.TypeName(v)}");

        private static int Int(FuncExpr f, BslValue v)
        {
            var d = decimal.Truncate(Num(f, v));
            if (d > int.MaxValue) return int.MaxValue;
            if (d < int.MinValue) return int.MinValue;
            return (int)d;
        }

        public static BslValue Call(FuncExpr f, EvalCtx c)
        {
            if (f.Name == "ISNULL")
            {
                var first = f.Args[0].Eval(c);
                return QV.IsNull(first) ? f.Args[1].Eval(c) : first;
            }
            var a = new BslValue[f.Args.Count];
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = f.Args[i].Eval(c);
                if (QV.IsNull(a[i]) && f.Name != "PRESENTATION") return QV.Null;
            }
            try
            {
                return Apply(f, a);
            }
            catch (OverflowException)
            {
                throw Bad(f, "переполнение");
            }
            catch (ArgumentOutOfRangeException)
            {
                throw Bad(f, "дата вне допустимого диапазона");
            }
        }

        private static BslValue Apply(FuncExpr f, BslValue[] a)
        {
            switch (f.Name)
            {
                // ---------- Строки ----------
                case "SUBSTRING":
                {
                    var s = Str(f, a[0]);
                    int start = Int(f, a[1]), len = Int(f, a[2]);
                    if (len < 0) throw Bad(f, "отрицательная длина");
                    long from = Math.Max(1L, start), to = Math.Min((long)s.Length, (long)start + len - 1);
                    return QV.Str(to < from ? "" : s.Substring((int)from - 1, (int)(to - from + 1)));
                }
                case "STRINGLENGTH": return QV.Num(Str(f, a[0]).Length);
                case "UPPER": return QV.Str(Str(f, a[0]).ToUpper(Ru));
                case "LOWER": return QV.Str(Str(f, a[0]).ToLower(Ru));
                case "TRIML": return QV.Str(Str(f, a[0]).TrimStart());
                case "TRIMR": return QV.Str(Str(f, a[0]).TrimEnd());
                case "TRIMALL": return QV.Str(Str(f, a[0]).Trim());
                case "LEFT":
                {
                    var s = Str(f, a[0]);
                    int n = Math.Clamp(Int(f, a[1]), 0, s.Length);
                    return QV.Str(s.Substring(0, n));
                }
                case "RIGHT":
                {
                    var s = Str(f, a[0]);
                    int n = Math.Clamp(Int(f, a[1]), 0, s.Length);
                    return QV.Str(s.Substring(s.Length - n));
                }
                case "STRFIND":
                {
                    var s = Str(f, a[0]);
                    var sub = Str(f, a[1]);
                    if (sub.Length == 0) return QV.Num(1);
                    return QV.Num(Ru.CompareInfo.IndexOf(s, sub, CompareOptions.IgnoreCase) + 1);
                }
                case "STRREPLACE":
                {
                    var s = Str(f, a[0]);
                    var what = Str(f, a[1]);
                    var with = Str(f, a[2]);
                    if (what.Length == 0) return QV.Str(s);
                    var sb = new StringBuilder();
                    int pos = 0;
                    while (true)
                    {
                        int k = s.IndexOf(what, pos, StringComparison.OrdinalIgnoreCase);
                        if (k < 0) break;
                        sb.Append(s, pos, k - pos).Append(with);
                        pos = k + what.Length;
                    }
                    sb.Append(s, pos, s.Length - pos);
                    return QV.Str(sb.ToString());
                }
                case "STRING":
                    return a[0] is BslPrimitiveValue
                        ? QV.Str(a[0] is BslUndefinedValue ? "" : QV.Presentation(a[0]))
                        : throw Bad(f, "ожидается примитивное значение");
                case "PRESENTATION":
                    return QV.Str(QV.IsNull(a[0]) || a[0] is BslUndefinedValue ? "" : QV.Presentation(a[0]));

                // ---------- Числа ----------
                case "ROUND":
                {
                    var x = Num(f, a[0]);
                    int digits = a.Length > 1 ? Int(f, a[1]) : 0;
                    if (digits >= 0) return QV.Num(Math.Round(x, Math.Min(digits, 28), MidpointRounding.AwayFromZero));
                    var scale = (decimal)Math.Pow(10, Math.Min(-digits, 28));
                    return QV.Num(Math.Round(x / scale, MidpointRounding.AwayFromZero) * scale);
                }
                case "INT": return QV.Num(decimal.Truncate(Num(f, a[0])));
                case "ACOS": return Math1(f, Math.Acos, a[0]);
                case "ASIN": return Math1(f, Math.Asin, a[0]);
                case "ATAN": return Math1(f, Math.Atan, a[0]);
                case "COS": return Math1(f, Math.Cos, a[0]);
                case "SIN": return Math1(f, Math.Sin, a[0]);
                case "TAN": return Math1(f, Math.Tan, a[0]);
                case "EXP": return Math1(f, Math.Exp, a[0]);
                case "LOG": return Math1(f, Math.Log, a[0]);
                case "LOG10": return Math1(f, Math.Log10, a[0]);
                case "SQRT": return Math1(f, Math.Sqrt, a[0]);
                case "POW": return Double(f, Math.Pow((double)Num(f, a[0]), (double)Num(f, a[1])));

                // ---------- Даты ----------
                case "YEAR": return QV.Num(Date(f, a[0]).Year);
                case "QUARTER": return QV.Num((Date(f, a[0]).Month - 1) / 3 + 1);
                case "MONTH": return QV.Num(Date(f, a[0]).Month);
                case "DAYOFYEAR": return QV.Num(Date(f, a[0]).DayOfYear);
                case "DAY": return QV.Num(Date(f, a[0]).Day);
                case "WEEK":
                {
                    var d = Date(f, a[0]);
                    // Неделя 1 содержит 1 января, недели начинаются с понедельника
                    int jan1 = Weekday(new DateTime(d.Year, 1, 1)) - 1;
                    return QV.Num((d.DayOfYear - 1 + jan1) / 7 + 1);
                }
                case "WEEKDAY": return QV.Num(Weekday(Date(f, a[0])));
                case "HOUR": return QV.Num(Date(f, a[0]).Hour);
                case "MINUTE": return QV.Num(Date(f, a[0]).Minute);
                case "SECOND": return QV.Num(Date(f, a[0]).Second);
                case "BEGINOFPERIOD": return QV.Date(BeginOf(Date(f, a[0]), Period(a[1])));
                case "ENDOFPERIOD":
                {
                    var p = Period(a[1]);
                    var begin = BeginOf(Date(f, a[0]), p);
                    // Последняя секунда периода: начало следующего периода минус секунда.
                    // Третья декада длится до конца месяца.
                    var next = p == "TENDAYS"
                        ? (begin.Day == 21 ? new DateTime(begin.Year, begin.Month, 1).AddMonths(1) : begin.AddDays(10))
                        : AddPeriod(begin, p, 1);
                    return QV.Date(next.AddSeconds(-1));
                }
                case "DATEADD":
                {
                    var d = Date(f, a[0]);
                    var n = decimal.Truncate(Num(f, a[2]));
                    if (Math.Abs(n) > 10_000_000_000m) throw Bad(f, "слишком большое количество");
                    return QV.Date(AddPeriod(d, Period(a[1]), (long)n));
                }
                case "DATEDIFF":
                {
                    var d1 = Date(f, a[0]);
                    var d2 = Date(f, a[1]);
                    return QV.Num(DateDiff(d1, d2, Period(a[2])));
                }
                case "DATETIME":
                {
                    int[] p = new int[6];
                    for (int i = 0; i < a.Length; i++) p[i] = Int(f, a[i]);
                    try { return QV.Date(new DateTime(p[0], p[1], p[2], p[3], p[4], p[5])); }
                    catch (ArgumentException) { throw Bad(f, "такой даты не существует"); }
                }

                // ---------- Прочее ----------
                case "VALUETYPE":
                    if (a[0] is BslUndefinedValue) return QV.Undefined;
                    try { return new BslTypeValue(a[0].SystemType); }
                    catch (Exception) { throw Bad(f, "тип значения не определён"); }
            }
            throw Bad(f, "функция не реализована");
        }

        private static string Period(BslValue v) => (string)(BslStringValue)v;

        private static BslValue Math1(FuncExpr f, Func<double, double> fn, BslValue v) => Double(f, fn((double)Num(f, v)));

        private static BslValue Double(FuncExpr f, double r)
        {
            if (double.IsNaN(r) || double.IsInfinity(r)) throw Bad(f, "значение вне области определения");
            try { return QV.Num((decimal)r); }
            catch (OverflowException) { throw Bad(f, "переполнение"); }
        }

        public static int Weekday(DateTime d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

        public static DateTime BeginOf(DateTime d, string period) => period switch
        {
            "SECOND" => new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second),
            "MINUTE" => new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0),
            "HOUR" => new DateTime(d.Year, d.Month, d.Day, d.Hour, 0, 0),
            "DAY" => d.Date,
            "WEEK" => d.Date.AddDays(1 - Weekday(d)),
            "TENDAYS" => new DateTime(d.Year, d.Month, d.Day <= 10 ? 1 : d.Day <= 20 ? 11 : 21),
            "MONTH" => new DateTime(d.Year, d.Month, 1),
            "QUARTER" => new DateTime(d.Year, (d.Month - 1) / 3 * 3 + 1, 1),
            "HALFYEAR" => new DateTime(d.Year, d.Month <= 6 ? 1 : 7, 1),
            _ => new DateTime(d.Year, 1, 1),
        };

        public static DateTime AddPeriod(DateTime d, string period, long n)
        {
            switch (period)
            {
                case "SECOND": return d.AddSeconds(n);
                case "MINUTE": return d.AddMinutes(n);
                case "HOUR": return d.AddHours(n);
                case "DAY": return d.AddDays(n);
                case "WEEK": return d.AddDays(7 * n);
                case "TENDAYS": return d.AddDays(10 * n);   // в ДОБАВИТЬКДАТЕ декада — ровно 10 дней
                case "MONTH": return d.AddMonths(checked((int)n));
                case "QUARTER": return d.AddMonths(checked((int)(3 * n)));
                case "HALFYEAR": return d.AddMonths(checked((int)(6 * n)));
                default: return d.AddYears(checked((int)n));
            }
        }

        public static decimal DateDiff(DateTime d1, DateTime d2, string unit)
        {
            switch (unit)
            {
                case "SECOND": return (long)(BeginOf(d2, "SECOND") - BeginOf(d1, "SECOND")).TotalSeconds;
                case "MINUTE": return (long)(BeginOf(d2, "MINUTE") - BeginOf(d1, "MINUTE")).TotalMinutes;
                case "HOUR": return (long)(BeginOf(d2, "HOUR") - BeginOf(d1, "HOUR")).TotalHours;
                case "DAY": return (long)(d2.Date - d1.Date).TotalDays;
                case "MONTH": return (d2.Year * 12 + d2.Month) - (d1.Year * 12 + d1.Month);
                case "QUARTER": return (d2.Year * 4 + (d2.Month - 1) / 3) - (d1.Year * 4 + (d1.Month - 1) / 3);
                default: return d2.Year - d1.Year;
            }
        }

        // ---------- ВЫРАЗИТЬ ----------

        public static bool CastTypeMatches(string type, BslValue v) => type switch
        {
            "NUMBER" => v is BslNumericValue,
            "STRING" => v is BslStringValue,
            "DATE" => v is BslDateValue,
            _ => v is BslBooleanValue,
        };

        public static BslValue Cast(CastExpr c, BslValue v)
        {
            if (QV.IsNull(v)) return QV.Null;
            // Значение другого типа (как у поля составного типа) становится NULL
            if (!CastTypeMatches(c.TypeName, v)) return QV.Null;
            switch (c.TypeName)
            {
                case "NUMBER":
                {
                    var x = (decimal)(BslNumericValue)v;
                    if (c.Length == null) return v;
                    int scale = Math.Min(c.Precision ?? 0, 28);
                    x = Math.Round(x, scale, MidpointRounding.AwayFromZero);
                    int intDigits = c.Length.Value - scale;
                    var limit = intDigits >= 28 ? decimal.MaxValue : (decimal)Math.Pow(10, Math.Max(intDigits, 0));
                    if (Math.Abs(decimal.Truncate(x)) >= limit)
                        throw new QueryException($"Переполнение при ВЫРАЗИТЬ: число {QV.Presentation(v)} не помещается в Число({c.Length}, {scale})", c.Pos, c.Len);
                    return QV.Num(x);
                }
                case "STRING":
                {
                    var s = (string)(BslStringValue)v;
                    if (c.Length is int n && n > 0 && s.Length > n) s = s.Substring(0, n);
                    return QV.Str(s);
                }
                default:
                    return v;
            }
        }
    }
}
