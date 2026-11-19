// Язык запросов: проверка текстов запросов в коде без запуска (для подсветки в редакторе).
// Находит строковые литералы BSL с текстом запроса, разбирает их и переводит позиции ошибок
// из текста запроса обратно в строку и столбец кода. Сканер литералов повторяет StringLexerState
// OneScript: строки продолжения с «|», удвоенные кавычки, соседние литералы "а" "б".
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OneScriptWasm.Query
{
    public sealed class QueryDiagnostic
    {
        public int Line;
        public int Column;
        public int Length;
        public string Message;
        public bool Warning;
    }

    internal sealed class BslStringLiteral
    {
        public string Value;
        public int[] SourceIndex;   // индекс в коде для каждого символа Value; последний элемент — закрывающая кавычка
        public int Start;           // открывающая кавычка
        public int End;             // закрывающая кавычка
    }

    public static class QueryChecker
    {
        private static readonly Regex QueryStart = new(@"^(?:\s|//[^\n]*\n?)*(?:ВЫБРАТЬ|SELECT|УНИЧТОЖИТЬ|DROP)(?=\s)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex QueryContext = new(@"(?:\.\s*(?:Текст|Text)\s*=|(?:Новый|New)\s+(?:Запрос|Query)\s*\()\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FromWord = new(@"(?<![\p{L}\p{N}_])(?:ИЗ|FROM)(?![\p{L}\p{N}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static List<QueryDiagnostic> Check(string code)
        {
            var result = new List<QueryDiagnostic>();
            if (string.IsNullOrEmpty(code)) return result;
            var lineStarts = LineStarts(code);
            foreach (var lit in FindLiterals(code))
            {
                if (!LooksLikeQuery(code, lit)) continue;
                foreach (var (pos, len, message, warning) in CheckText(lit.Value))
                {
                    int k = Math.Clamp(pos, 0, lit.Value.Length);
                    int start = lit.SourceIndex[k];
                    int endK = Math.Clamp(pos + Math.Max(len, 1), 0, lit.Value.Length);
                    int end = Math.Max(lit.SourceIndex[endK], start + 1);
                    var (line, col) = ToLineColumn(lineStarts, start);
                    var (endLine, _) = ToLineColumn(lineStarts, end - 1);
                    int length = endLine == line ? end - start : LineEnd(code, start) - start;
                    result.Add(new QueryDiagnostic { Line = line, Column = col, Length = Math.Max(length, 1), Message = message, Warning = warning });
                }
            }
            return result;
        }

        /// <summary>Ошибки и предупреждения одного текста запроса: (позиция, длина, текст, предупреждение).</summary>
        internal static List<(int Pos, int Len, string Message, bool Warning)> CheckText(string text)
        {
            var list = new List<(int, int, string, bool)>();
            List<Statement> batch;
            try
            {
                batch = QueryParser.ParseBatch(text);
            }
            catch (QueryException e)
            {
                list.Add((e.Pos, e.Length, e.Message, false));
                return list;
            }
            foreach (var st in batch)
            {
                if (st is not SelectStatement s) continue;
                var q = s.Query;
                var paramSources = Sources(q.Cores[0]).Where(x => x.Kind == SourceKind.Param).ToList();
                if (q.Into == null)
                {
                    foreach (var p in AllCores(q).SelectMany(Sources).Where(x => x.Kind == SourceKind.Param))
                        list.Add((p.Pos, p.Len,
                            "В 1С таблицу значений из параметра можно выбрать только во временную таблицу: " +
                            "ВЫБРАТЬ … ПОМЕСТИТЬ ВТ ИЗ &" + p.Name + " КАК Т, а остальное — следующим запросом пакета. Песочница выполнит и так", true));
                }
                else if (paramSources.Count > 0 && (q.Cores.Count > 1 || q.Cores[0].From.Any(f => f.Joins.Count > 0) || q.Cores[0].From.Count > 1))
                {
                    var p = paramSources[0];
                    list.Add((p.Pos, p.Len,
                        "В 1С запрос, который помещает таблицу значений во временную таблицу, не может содержать соединений и объединений. Песочница выполнит и так", true));
                }
                foreach (var src in AllCores(q).SelectMany(Sources).Where(x => x.Kind == SourceKind.Table && x.Name.Contains('.')))
                    list.Add((src.Pos, src.Len,
                        $"В песочнице нет информационной базы: таблицы \"{src.Name}\" нет. Передайте данные таблицей значений через параметр (ИЗ &Таблица КАК Т)", true));
            }
            return list;
        }

        private static IEnumerable<SelectCore> AllCores(QueryNode q)
        {
            foreach (var core in q.Cores)
            {
                yield return core;
                foreach (var src in Sources(core))
                    if (src.Sub != null)
                        foreach (var c in AllCores(src.Sub)) yield return c;
            }
        }

        private static IEnumerable<SourceRef> Sources(SelectCore core) =>
            core.From.SelectMany(f => new[] { f.Source }.Concat(f.Joins.Select(j => j.Source)));

        private static bool LooksLikeQuery(string code, BslStringLiteral lit)
        {
            if (!QueryStart.IsMatch(lit.Value)) return false;
            // Текст, собранный конкатенацией, статически не проверяется: он может быть незаконченным
            int before = lit.Start - 1;
            while (before >= 0 && char.IsWhiteSpace(code[before])) before--;
            int after = lit.End + 1;
            while (after < code.Length && char.IsWhiteSpace(code[after])) after++;
            if (before >= 0 && code[before] == '+' || after < code.Length && code[after] == '+') return false;
            var prefix = code.Substring(Math.Max(0, lit.Start - 200), Math.Min(200, lit.Start));
            if (QueryContext.IsMatch(prefix)) return true;
            return lit.Value.Contains('\n') && FromWord.IsMatch(lit.Value);
        }

        internal static List<BslStringLiteral> FindLiterals(string code)
        {
            var result = new List<BslStringLiteral>();
            int i = 0, n = code.Length;
            while (i < n)
            {
                char c = code[i];
                if (c == '/' && i + 1 < n && code[i + 1] == '/') { while (i < n && code[i] != '\n') i++; continue; }
                if (c == '\'') { i++; while (i < n && code[i] != '\'' && code[i] != '\n') i++; i++; continue; }
                if (c != '"') { i++; continue; }

                var lit = new BslStringLiteral { Start = i };
                var sb = new StringBuilder();
                var idx = new List<int>();
                i++;
                bool closed = false;
                while (i < n)
                {
                    c = code[i];
                    if (c == '"')
                    {
                        if (i + 1 < n && code[i + 1] == '"') { sb.Append('"'); idx.Add(i); i += 2; continue; }
                        int close = i;
                        int j = SkipSpacesAndComments(code, i + 1);
                        if (j < n && code[j] == '"') { sb.Append('\n'); idx.Add(close); i = j + 1; continue; }
                        idx.Add(close);
                        lit.End = close;
                        i = close + 1;
                        closed = true;
                        break;
                    }
                    if (c == '\n')
                    {
                        int j = SkipSpacesAndComments(code, i + 1);
                        if (j >= n || code[j] != '|') break;   // незакрытая кавычка: о ней сообщит компилятор
                        sb.Append('\n'); idx.Add(i); i = j + 1;
                        continue;
                    }
                    if (c != '\r') { sb.Append(c); idx.Add(i); }
                    i++;
                }
                if (!closed) continue;
                lit.Value = sb.ToString();
                lit.SourceIndex = idx.ToArray();
                result.Add(lit);
            }
            return result;
        }

        private static int SkipSpacesAndComments(string code, int i)
        {
            int n = code.Length;
            while (i < n)
            {
                if (char.IsWhiteSpace(code[i])) { i++; continue; }
                if (code[i] == '/' && i + 1 < n && code[i + 1] == '/') { while (i < n && code[i] != '\n') i++; continue; }
                break;
            }
            return i;
        }

        private static List<int> LineStarts(string code)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < code.Length; i++) if (code[i] == '\n') starts.Add(i + 1);
            return starts;
        }

        private static (int Line, int Column) ToLineColumn(List<int> starts, int index)
        {
            int k = starts.BinarySearch(index);
            if (k < 0) k = ~k - 1;
            return (k + 1, index - starts[k] + 1);
        }

        private static int LineEnd(string code, int index)
        {
            int e = code.IndexOf('\n', index);
            return e < 0 ? code.Length : (e > 0 && code[e - 1] == '\r' ? e - 1 : e);
        }
    }
}
