// Хост OneScript для браузера: выполняет код на языке 1С и возвращает вывод в JSON.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using OneScript.Language;
using OneScript.Sources;
using OneScript.StandardLibrary;
using ScriptEngine.HostedScript;
using ScriptEngine.HostedScript.Extensions;
using ScriptEngine.Hosting;

namespace OneScriptWasm
{
    public class OutputEntry
    {
        public string Kind;   // "out" | "err"
        public string Text;
    }

    // Перехватывает Console.Out (объект Консоль) и пишет в общий вывод в порядке вызовов,
    // чтобы строки Консоль.ВывестиСтроку не обгоняли Сообщить.
    internal sealed class OrderedConsoleWriter : TextWriter
    {
        private readonly List<OutputEntry> _output;
        private readonly StringBuilder _line = new();
        public OrderedConsoleWriter(List<OutputEntry> output) { _output = output; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            if (value == '\r') return;
            if (value == '\n') { FlushLine(); return; }
            _line.Append(value);
        }
        public void FlushLine()
        {
            _output.Add(new OutputEntry { Kind = "out", Text = _line.ToString() });
            _line.Clear();
        }
        public void FlushTail() { if (_line.Length > 0) FlushLine(); }
    }

    internal sealed class BrowserHost : IHostApplication
    {
        public readonly List<OutputEntry> Output = new();
        private readonly Queue<string> _input;
        public OrderedConsoleWriter ConsoleWriter;

        public BrowserHost(string input)
        {
            _input = new Queue<string>((input ?? "").Replace("\r\n", "\n").Split('\n'));
        }

        public void Echo(string text, MessageStatusEnum status = MessageStatusEnum.Ordinary)
        {
            ConsoleWriter?.FlushTail();
            var kind = status == MessageStatusEnum.Important || status == MessageStatusEnum.VeryImportant ? "err" : "out";
            Output.Add(new OutputEntry { Kind = kind, Text = text });
        }

        public void ShowExceptionInfo(Exception exc)
        {
            ConsoleWriter?.FlushTail();
            Output.Add(new OutputEntry { Kind = "err", Text = Runner.Describe(exc) });
        }

        public bool InputString(out string result, string prompt, int maxLen, bool multiline)
        {
            if (_input.Count == 0) { result = ""; return false; }
            result = _input.Dequeue();
            return true;
        }

        public string[] GetCommandLineArguments() => Array.Empty<string>();
    }

    public static partial class Runner
    {
        private const string NotInBrowser =
            "Эта возможность недоступна в браузере: песочница не работает с сетью, процессами и внешними ресурсами.";

        private static bool IsNotSupported(Exception exc)
        {
            for (var e = exc; e != null; e = e.InnerException)
                if (e is PlatformNotSupportedException) return true;
            return exc.Message.Contains("PlatformNotSupportedException");
        }

        // Для знаков OneScript печатает внутреннее имя токена («Ожидается символ: ClosePar»)
        private static readonly (string Name, string Text)[] TokenNames =
        {
            ("OpenPar", "("), ("ClosePar", ")"), ("OpenBracket", "["), ("CloseBracket", "]"),
            ("Comma", ","), ("Dot", "."), ("Equal", "="), ("Semicolon", ";"), ("Question", "?"),
        };

        internal static string Humanize(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            foreach (var (name, text) in TokenNames)
                message = System.Text.RegularExpressions.Regex.Replace(message, $@"\b{name}\b", text);
            return message;
        }

        internal static string Describe(Exception exc)
        {
            if (exc is ScriptException se)
            {
                // Ошибка внутри заглушки БСП: номер строки относится к ней, а не к коду пользователя
                if (!string.IsNullOrEmpty(se.ModuleName) && se.ModuleName != ModuleName)
                    return $"{se.ModuleName} (заглушка БСП, строка {se.LineNumber}): " + Humanize(se.ErrorDescription);
                var line = se.LineNumber > 0 ? $"Строка {se.LineNumber}: " : "";
                return line + (IsNotSupported(se) ? NotInBrowser : Humanize(se.ErrorDescription));
            }
            if (IsNotSupported(exc)) return NotInBrowser;
            return exc.GetType().Name + ": " + exc.Message;
        }

        // Имя модуля с кодом пользователя: его видно в ОписаниеОшибки() — «{Песочница(3)}: …»
        internal const string ModuleName = "Песочница";

        // Предел глубины вызовов: бесконечная рекурсия даёт понятную ошибку, а не ждёт таймаута
        private const int MaxCallDepth = 3000;

        // ---------- Заглушки БСП ----------
        // Модули на языке 1С (browser/bsp/*.bsl, ресурсы сборки). Подключаются глобальными свойствами,
        // только если код к ним обращается: компиляция заглушек на каждый запуск стоила бы времени.
        // Порядок важен: ОбщегоНазначения вызывает ОбщегоНазначенияКлиентСервер.
        private static readonly string[] BspOrder =
            { "ОбщегоНазначенияКлиентСервер", "ОбщегоНазначения", "СтроковыеФункцииКлиентСервер" };
        private static List<(string Name, string Code)> _bspModules;

        private static List<(string Name, string Code)> BspModules()
        {
            if (_bspModules != null) return _bspModules;
            var asm = typeof(Runner).Assembly;
            var list = new List<(string, string)>();
            foreach (var name in BspOrder)
            {
                using var stream = asm.GetManifestResourceStream("bsp." + name)
                    ?? throw new InvalidOperationException("Нет ресурса заглушки БСП: " + name);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                list.Add((name, reader.ReadToEnd()));
            }
            return _bspModules = list;
        }

        private static bool Mentions(string code, string module) =>
            System.Text.RegularExpressions.Regex.IsMatch(code ?? "", $@"(?<![\p{{L}}\p{{N}}_]){module}\s*\.");

        private static void AttachBsp(HostedScriptEngine engine, string code)
        {
            var modules = BspModules();
            // Нужные модули и те, от которых они зависят (ОбщегоНазначения → ОбщегоНазначенияКлиентСервер)
            var need = new HashSet<string>();
            foreach (var (name, _) in modules)
                if (Mentions(code, name)) need.Add(name);
            if (need.Count == 0) return;
            if (need.Contains("ОбщегоНазначения")) need.Add("ОбщегоНазначенияКлиентСервер");

            engine.Initialize();
            var factory = engine.Engine.AttachedScriptsFactory;
            foreach (var (name, text) in modules)
            {
                if (!need.Contains(name)) continue;
                var process = engine.Engine.NewProcess();
                var src = OneScript.Sources.SourceCodeBuilder.Create().FromString(text).WithName(name).Build();
                var module = factory.CompileModuleFromSource(engine.GetCompilerService(), src, null, process);
                var instance = engine.Engine.NewObject(module, process);
                engine.InjectGlobalProperty(name, name, instance, true);
            }
        }

        private static void SetCulture()
        {
            var ru = new System.Globalization.CultureInfo("ru-RU");
            System.Globalization.CultureInfo.CurrentCulture = ru;
            System.Globalization.CultureInfo.CurrentUICulture = ru;
        }

        // errorSink == null: стандартное поведение (исключение на первой ошибке компиляции).
        // ListErrorSink: компилятор собирает все ошибки и продолжает разбор.
        private static HostedScriptEngine CreateEngine(IErrorSink errorSink)
        {
            var builder = DefaultEngineBuilder.Create()
                .SetupConfiguration(p => { });
            builder.SetDefaultOptions()
                .UseImports()
                .UseBinaryDataOptions()
                .UseBackgroundTasksOptions()
                .UseNativeRuntime()
                .UseEventHandlers();
            if (errorSink != null)
                builder.Services.RegisterSingleton<IErrorSink>(_ => errorSink);
            builder.SetupEnvironment(env =>
            {
                env.AddStandardLibrary();
                // Язык запросов: Запрос, РезультатЗапроса, МенеджерВременныхТаблиц, ОбходРезультатаЗапроса
                env.AddAssembly(typeof(Runner).Assembly, t => t.Namespace == "OneScriptWasm.Query");
            });
            ScriptEngine.Machine.MachineInstance.SandboxMaxCallDepth = MaxCallDepth;
            return new HostedScriptEngine(builder.Build());
        }

        private static SourceCode MakeSource(string code) =>
            OneScript.Sources.SourceCodeBuilder.Create()
                .FromString(code ?? "")
                .WithName(ModuleName)
                .Build();

        private sealed class Diagnostic
        {
            public int Line;
            public int Column;   // 1-based; 0 — столбец неизвестен
            public string Message;
            public int Length;      // сколько символов подчеркнуть; 0 — решает редактор
            public bool Warning;
            public string Source;   // «Запрос» для ошибок в тексте запроса; null — компилятор
        }

        /// <summary>
        /// Компилирует код без выполнения и возвращает все найденные ошибки:
        /// [{line, column, message}]. Синтаксические ошибки парсер собирает с восстановлением
        /// (пропускает до следующего оператора), ошибки связывания (неизвестные символы,
        /// число аргументов) добавляет генератор кода.
        /// </summary>
        public static string CheckToJson(string code)
        {
            SetCulture();
            var sink = new ListErrorSink();
            var extra = new List<Diagnostic>();
            var oldOut = Console.Out;
            Console.SetOut(TextWriter.Null);
            OneScript.Language.SyntaxAnalysis.BslSyntaxWalker.SandboxCheckMode = true;
            try
            {
                using var engine = CreateEngine(sink);
                AttachBsp(engine, code);
                // CreateProcess только компилирует модуль и создаёт процесс; код выполняется в Start(), его не вызываем.
                engine.CreateProcess(new BrowserHost(""), MakeSource(code));
            }
            catch (ScriptException se)
            {
                extra.Add(new Diagnostic { Line = se.LineNumber, Column = se.ColumnNumber, Message = Humanize(se.ErrorDescription) });
            }
            catch (Exception)
            {
                // Генератор кода может упасть на дереве, испорченном синтаксической ошибкой.
                // Ошибки, собранные до этого момента, всё равно отдаём.
            }
            finally
            {
                OneScript.Language.SyntaxAnalysis.BslSyntaxWalker.SandboxCheckMode = false;
                Console.SetOut(oldOut);
            }

            var all = new List<Diagnostic>();
            var seen = new HashSet<string>();
            void Add(Diagnostic d)
            {
                if (d.Line <= 0 || string.IsNullOrEmpty(d.Message)) return;
                if (seen.Add($"{d.Line}:{d.Column}:{d.Message}")) all.Add(d);
            }
            foreach (var e in sink.Errors)
                Add(new Diagnostic
                {
                    Line = e.Position?.LineNumber ?? 0,
                    Column = Math.Max(0, e.Position?.ColumnNumber ?? 0),
                    Message = Humanize(e.Description),
                });
            foreach (var d in extra) Add(d);

            // Тексты запросов в строковых литералах: синтаксис и совместимость с платформой
            try
            {
                foreach (var q in OneScriptWasm.Query.QueryChecker.Check(code))
                    Add(new Diagnostic { Line = q.Line, Column = q.Column, Length = q.Length, Message = q.Message, Warning = q.Warning, Source = "Запрос" });
            }
            catch (Exception)
            {
                // Проверка запросов — подсказка; её сбой не должен ломать проверку кода
            }

            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartArray();
                foreach (var d in all)
                {
                    w.WriteStartObject();
                    w.WriteNumber("line", d.Line);
                    w.WriteNumber("column", d.Column);
                    w.WriteString("message", d.Message);
                    if (d.Length > 0) w.WriteNumber("length", d.Length);
                    if (d.Warning) w.WriteString("severity", "warning");
                    if (d.Source != null) w.WriteString("source", d.Source);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public static string RunToJson(string code, string input)
        {
            SetCulture();
            var host = new BrowserHost(input);
            var sw = Stopwatch.StartNew();
            int exitCode;
            var consoleOut = new OrderedConsoleWriter(host.Output);
            host.ConsoleWriter = consoleOut;
            var oldOut = Console.Out;
            Console.SetOut(consoleOut);
            try
            {
                using var engine = CreateEngine(null);
                var source = MakeSource(code);
                ScriptEngine.HostedScript.Process process;
                try
                {
                    AttachBsp(engine, code);
                    process = engine.CreateProcess(host, source);
                }
                catch (Exception e)
                {
                    host.ShowExceptionInfo(e);
                    consoleOut.FlushTail();
                    return Serialize(host, 1, sw.ElapsedMilliseconds);
                }
                exitCode = process.Start();
            }
            catch (Exception e)
            {
                host.Output.Add(new OutputEntry { Kind = "err", Text = "Внутренняя ошибка движка: " + e });
                exitCode = 2;
            }
            finally
            {
                consoleOut.FlushTail();
                Console.SetOut(oldOut);
            }
            return Serialize(host, exitCode, sw.ElapsedMilliseconds);
        }

        private static string Serialize(BrowserHost host, int exitCode, long ms)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteNumber("exitCode", exitCode);
                w.WriteNumber("ms", ms);
                w.WriteStartArray("output");
                foreach (var e in host.Output)
                {
                    w.WriteStartObject(); w.WriteString("kind", e.Kind); w.WriteString("text", e.Text); w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        [JSExport]
        public static string Run(string code, string input) => RunToJson(code, input);

        [JSExport]
        public static string Check(string code) => CheckToJson(code);

        [JSExport]
        public static string Version() => typeof(ScriptException).Assembly.GetName().Version?.ToString() ?? "?";
    }

    public static class Program
    {
        public static void Main(string[] args)
        {
            // В браузере Main ничего не делает: JS вызывает Runner.Run напрямую.
            if (args.Length > 1 && args[0] == "--check" && File.Exists(args[1]))
            {
                Console.WriteLine(Runner.CheckToJson(File.ReadAllText(args[1])));
                return;
            }
            if (args.Length > 0 && File.Exists(args[0]))
            {
                Console.WriteLine(Runner.RunToJson(File.ReadAllText(args[0]), args.Length > 1 ? args[1] : ""));
            }
        }
    }
}
