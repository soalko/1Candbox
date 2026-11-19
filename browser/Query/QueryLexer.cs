// Язык запросов: лексер. Ключевые слова на русском и английском, регистр не важен,
// комментарии //, строки "..." с удвоением кавычки, числа с точкой, параметры &Имя.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OneScriptWasm.Query
{
    internal enum TokKind { Ident, Keyword, Number, String, Param, Op, End }

    internal sealed class Token
    {
        public TokKind Kind;
        public string Text;    // как написано в тексте
        public string Kw;      // каноническое имя ключевого слова (SELECT, FROM…) или null
        public string Value;   // содержимое строки, имя параметра или идентификатора
        public decimal Number;
        public int Pos;        // смещение в тексте запроса
        public int Length;

        public bool Is(string kw) => Kind == TokKind.Keyword && Kw == kw;
        public bool IsOp(string op) => Kind == TokKind.Op && Text == op;
        public override string ToString() => Kind == TokKind.End ? "конец текста" : Text;
    }

    internal static class QueryLexer
    {
        // Зарезервированные (структурные) слова. Имена функций, агрегатов, типов и периодов
        // не зарезервированы: «СУММА(Т.Сумма) КАК Сумма» и «Т.Количество» — обычный текст запроса.
        private static readonly Dictionary<string, string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ВЫБРАТЬ"] = "SELECT", ["SELECT"] = "SELECT",
            ["РАЗРЕШЕННЫЕ"] = "ALLOWED", ["ALLOWED"] = "ALLOWED",
            ["РАЗЛИЧНЫЕ"] = "DISTINCT", ["DISTINCT"] = "DISTINCT",
            ["ПЕРВЫЕ"] = "TOP", ["TOP"] = "TOP",
            ["КАК"] = "AS", ["AS"] = "AS",
            ["ПОМЕСТИТЬ"] = "INTO", ["INTO"] = "INTO",
            ["ИЗ"] = "FROM", ["FROM"] = "FROM",
            ["ГДЕ"] = "WHERE", ["WHERE"] = "WHERE",
            ["СГРУППИРОВАТЬ"] = "GROUP", ["GROUP"] = "GROUP",
            ["ПО"] = "BY", ["BY"] = "BY", ["ON"] = "BY",
            ["ИМЕЮЩИЕ"] = "HAVING", ["HAVING"] = "HAVING",
            ["УПОРЯДОЧИТЬ"] = "ORDER", ["ORDER"] = "ORDER",
            ["ВОЗР"] = "ASC", ["ASC"] = "ASC",
            ["УБЫВ"] = "DESC", ["DESC"] = "DESC",
            ["ОБЪЕДИНИТЬ"] = "UNION", ["UNION"] = "UNION",
            ["УНИЧТОЖИТЬ"] = "DROP", ["DROP"] = "DROP",
            ["ИНДЕКСИРОВАТЬ"] = "INDEX", ["INDEX"] = "INDEX",
            ["ДЛЯ"] = "FOR", ["FOR"] = "FOR",
            ["АВТОУПОРЯДОЧИВАНИЕ"] = "AUTOORDER", ["AUTOORDER"] = "AUTOORDER",
            ["ИТОГИ"] = "TOTALS", ["TOTALS"] = "TOTALS",
            ["СОЕДИНЕНИЕ"] = "JOIN", ["JOIN"] = "JOIN",
            ["ВНУТРЕННЕЕ"] = "INNER", ["INNER"] = "INNER",
            ["ЛЕВОЕ"] = "LEFT", ["LEFT"] = "LEFT",
            ["ПРАВОЕ"] = "RIGHT", ["RIGHT"] = "RIGHT",
            ["ПОЛНОЕ"] = "FULL", ["FULL"] = "FULL",
            ["ВНЕШНЕЕ"] = "OUTER", ["OUTER"] = "OUTER",
            ["И"] = "AND", ["AND"] = "AND",
            ["ИЛИ"] = "OR", ["OR"] = "OR",
            ["НЕ"] = "NOT", ["NOT"] = "NOT",
            ["В"] = "IN", ["IN"] = "IN",
            ["МЕЖДУ"] = "BETWEEN", ["BETWEEN"] = "BETWEEN",
            ["ПОДОБНО"] = "LIKE", ["LIKE"] = "LIKE",
            ["СПЕЦСИМВОЛ"] = "ESCAPE", ["ESCAPE"] = "ESCAPE",
            ["ЕСТЬ"] = "IS", ["IS"] = "IS",
            ["NULL"] = "NULL",
            ["ВЫБОР"] = "CASE", ["CASE"] = "CASE",
            ["КОГДА"] = "WHEN", ["WHEN"] = "WHEN",
            ["ТОГДА"] = "THEN", ["THEN"] = "THEN",
            ["ИНАЧЕ"] = "ELSE", ["ELSE"] = "ELSE",
            ["КОНЕЦ"] = "END", ["END"] = "END",
            ["ИСТИНА"] = "TRUE", ["TRUE"] = "TRUE",
            ["ЛОЖЬ"] = "FALSE", ["FALSE"] = "FALSE",
            ["НЕОПРЕДЕЛЕНО"] = "UNDEFINED", ["UNDEFINED"] = "UNDEFINED",
            ["ССЫЛКА"] = "REFS", ["REFS"] = "REFS",
        };

        public static List<Token> Tokenize(string text)
        {
            var list = new List<Token>();
            int i = 0, n = text.Length;
            while (true)
            {
                // Пробелы и комментарии
                while (i < n)
                {
                    if (char.IsWhiteSpace(text[i])) { i++; continue; }
                    if (text[i] == '/' && i + 1 < n && text[i + 1] == '/')
                    {
                        while (i < n && text[i] != '\n') i++;
                        continue;
                    }
                    break;
                }
                if (i >= n)
                {
                    list.Add(new Token { Kind = TokKind.End, Text = "", Pos = n });
                    return list;
                }

                int start = i;
                char c = text[i];
                if (char.IsLetter(c) || c == '_')
                {
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    var word = text.Substring(start, i - start);
                    var tok = new Token { Text = word, Value = word, Pos = start, Length = i - start };
                    if (Keywords.TryGetValue(word, out var kw)) { tok.Kind = TokKind.Keyword; tok.Kw = kw; }
                    else tok.Kind = TokKind.Ident;
                    list.Add(tok);
                    continue;
                }
                if (char.IsDigit(c))
                {
                    while (i < n && char.IsDigit(text[i])) i++;
                    if (i + 1 < n && text[i] == '.' && char.IsDigit(text[i + 1]))
                    {
                        i++;
                        while (i < n && char.IsDigit(text[i])) i++;
                    }
                    var s = text.Substring(start, i - start);
                    if (i < n && (char.IsLetter(text[i]) || text[i] == '_'))
                        throw new QueryException($"Синтаксическая ошибка \"{s}{text[i]}\"", start, i - start + 1);
                    decimal value;
                    try { value = decimal.Parse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture); }
                    catch (OverflowException) { throw new QueryException($"Слишком большое число \"{s}\"", start, s.Length); }
                    list.Add(new Token { Kind = TokKind.Number, Text = s, Number = value, Pos = start, Length = s.Length });
                    continue;
                }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    bool closed = false;
                    while (i < n)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < n && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                            i++;
                            closed = true;
                            break;
                        }
                        if (text[i] != '\r') sb.Append(text[i]);
                        i++;
                    }
                    if (!closed)
                        throw new QueryException("Строковый литерал не закрыт: нет закрывающей кавычки", start, 1);
                    list.Add(new Token { Kind = TokKind.String, Text = text.Substring(start, i - start), Value = sb.ToString(), Pos = start, Length = i - start });
                    continue;
                }
                if (c == '&')
                {
                    i++;
                    int nameStart = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    if (i == nameStart || char.IsDigit(text[nameStart]))
                        throw new QueryException("Ожидается имя параметра после \"&\"", start, 1);
                    list.Add(new Token { Kind = TokKind.Param, Text = text.Substring(start, i - start), Value = text.Substring(nameStart, i - nameStart), Pos = start, Length = i - start });
                    continue;
                }
                string op = null;
                if (i + 1 < n)
                {
                    var two = text.Substring(i, 2);
                    if (two == "<>" || two == "<=" || two == ">=") op = two;
                }
                if (op == null && "(),.;*+-/=<>".IndexOf(c) >= 0) op = c.ToString();
                if (op == null)
                {
                    var hint = c == '%' ? ": в языке запросов нет операции остатка от деления" : "";
                    throw new QueryException($"Синтаксическая ошибка \"{c}\"{hint}", start, 1);
                }
                i += op.Length;
                list.Add(new Token { Kind = TokKind.Op, Text = op, Pos = start, Length = op.Length });
            }
        }
    }
}
