// Язык запросов: значения, сравнение и равенство по правилам запросов 1С.
// Порядок типов: NULL < Неопределено < Булево < Число < Дата < Строка < прочие (ссылки).
// Строки сравниваются без учёта регистра и без хвостовых пробелов, как в СУБД платформы.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using OneScript.Exceptions;
using OneScript.Values;
using ScriptEngine.Machine;

namespace OneScriptWasm.Query
{
    internal static class QV
    {
        public static readonly BslValue Null = BslNullValue.Instance;
        public static readonly BslValue Undefined = BslUndefinedValue.Instance;
        public static readonly BslValue True = BslBooleanValue.True;
        public static readonly BslValue False = BslBooleanValue.False;

        private static readonly CompareInfo Ru = new CultureInfo("ru-RU").CompareInfo;
        private const CompareOptions StrOptions = CompareOptions.IgnoreCase;

        public static BslValue Bool(bool b) => b ? True : False;
        public static BslValue Num(decimal d) => BslNumericValue.Create(d);
        public static BslValue Str(string s) => BslStringValue.Create(s ?? "");
        public static BslValue Date(DateTime d) => BslDateValue.Create(d);

        /// <summary>Разворачивает ссылки на переменные и null из C# в «сырое» значение.</summary>
        public static BslValue Raw(IValue v) => v switch
        {
            null => Undefined,
            IValueReference r => r.BslValue,
            BslValue b => b,
            _ => Undefined,
        };

        public static bool IsNull(BslValue v) => v is BslNullValue;

        /// <summary>Ранг типа для сравнения значений разных типов.</summary>
        public static int Rank(BslValue v) => v switch
        {
            BslNullValue => 0,
            BslUndefinedValue => 1,
            BslBooleanValue => 2,
            BslNumericValue => 3,
            BslDateValue => 4,
            BslStringValue => 5,
            BslTypeValue => 6,
            _ => 7,
        };

        public static string TrimEndSpaces(string s) => s.TrimEnd(' ');

        public static int CompareStrings(string a, string b) =>
            Ru.Compare(TrimEndSpaces(a), TrimEndSpaces(b), StrOptions);

        /// <summary>Полный порядок для УПОРЯДОЧИТЬ ПО, МИНИМУМ и МАКСИМУМ. NULL — наименьшее значение.</summary>
        public static int Compare(BslValue a, BslValue b)
        {
            int ra = Rank(a), rb = Rank(b);
            if (ra != rb) return ra.CompareTo(rb);
            switch (a)
            {
                case BslNullValue:
                case BslUndefinedValue:
                    return 0;
                case BslBooleanValue ba:
                    return ((bool)ba).CompareTo((bool)(BslBooleanValue)b);
                case BslNumericValue na:
                    return ((decimal)na).CompareTo((decimal)(BslNumericValue)b);
                case BslDateValue da:
                    return ((DateTime)da).CompareTo((DateTime)(BslDateValue)b);
                case BslStringValue sa:
                    return CompareStrings((string)sa, (string)(BslStringValue)b);
                case BslTypeValue ta:
                    return string.CompareOrdinal(ta.TypeValue.Name, ((BslTypeValue)b).TypeValue.Name);
                default:
                    if (ReferenceEquals(a, b) || a.Equals(b)) return 0;
                    return string.CompareOrdinal(Presentation(a), Presentation(b));
            }
        }

        /// <summary>Равенство для ключей (группировка, РАЗЛИЧНЫЕ, ОБЪЕДИНИТЬ, хеш соединения): NULL равен NULL.</summary>
        public static bool KeyEquals(BslValue a, BslValue b)
        {
            if (Rank(a) != Rank(b)) return false;
            return a switch
            {
                BslNullValue or BslUndefinedValue => true,
                BslStringValue sa => CompareStrings((string)sa, (string)(BslStringValue)b) == 0,
                BslNumericValue na => (decimal)na == (decimal)(BslNumericValue)b,
                BslBooleanValue ba => (bool)ba == (bool)(BslBooleanValue)b,
                BslDateValue da => (DateTime)da == (DateTime)(BslDateValue)b,
                BslTypeValue ta => ta.Equals((BslTypeValue)b),
                _ => ReferenceEquals(a, b) || a.Equals(b),
            };
        }

        public static int KeyHash(BslValue v) => v switch
        {
            BslNullValue => 1,
            BslUndefinedValue => 2,
            BslStringValue s => Ru.GetHashCode(TrimEndSpaces((string)s), StrOptions),
            BslNumericValue n => ((decimal)n).GetHashCode(),
            BslBooleanValue b => ((bool)b) ? 3 : 4,
            BslDateValue d => ((DateTime)d).GetHashCode(),
            BslTypeValue t => t.TypeValue.GetHashCode(),
            _ => RuntimeHelpers.GetHashCode(v),
        };

        /// <summary>Сравнение «=» в условии: NULL с чем угодно даёт NULL (неизвестно).</summary>
        public static BslValue SqlEquals(BslValue a, BslValue b)
        {
            if (IsNull(a) || IsNull(b)) return Null;
            return Bool(KeyEquals(a, b));
        }

        /// <summary>Значение условия: ИСТИНА → true; ЛОЖЬ и NULL → false.</summary>
        public static bool IsTrue(BslValue v) => v is BslBooleanValue b && (bool)b;

        public static string TypeName(BslValue v)
        {
            try { return v.SystemType.Name; }
            catch (Exception) { return v.GetType().Name; }
        }

        public static string Presentation(BslValue v)
        {
            try { return v.ToString(); }
            catch (Exception) { return ""; }
        }

        /// <summary>Значение, которое можно выбрать в запросе из таблицы значений.</summary>
        public static bool IsSelectable(BslValue v) => v is BslPrimitiveValue || v is BslTypeValue;
    }

    /// <summary>Ключ из нескольких значений: строка результата, ключ группы или соединения.</summary>
    internal sealed class RowKeyComparer : IEqualityComparer<BslValue[]>
    {
        public static readonly RowKeyComparer Instance = new();

        public bool Equals(BslValue[] x, BslValue[] y)
        {
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
                if (!QV.KeyEquals(x[i], y[i])) return false;
            return true;
        }

        public int GetHashCode(BslValue[] key)
        {
            var h = new HashCode();
            foreach (var v in key) h.Add(QV.KeyHash(v));
            return h.ToHashCode();
        }
    }

    /// <summary>Ошибка запроса с позицией в тексте. На границе Выполнить() превращается в RuntimeException.</summary>
    internal sealed class QueryException : Exception
    {
        public int Pos { get; }
        public int Length { get; }

        public QueryException(string message, int pos, int length = 0) : base(message)
        {
            Pos = pos;
            Length = length;
        }
    }
}
