# Адаптирует OneScript для работы в браузере (WebAssembly).
# Скрипт можно запускать повторно: уже применённые правки пропускаются.
import pathlib, sys

root = pathlib.Path(sys.argv[1])

def patch(rel, old, new, label):
    p = root / rel
    s = p.read_text(encoding='utf-8-sig')
    if new in s:
        print('уже применено:', label)
        return
    if old not in s:
        sys.exit(f'Не найден фрагмент для правки «{label}» в {rel}. Возможно, исходники OneScript изменились.')
    p.write_text(s.replace(old, new, 1), encoding='utf-8')
    print('исправлено:', label)

# 1. Встроенный веб-сервер (ASP.NET Core) в браузере не нужен и не собирается под WebAssembly.
p = root / 'src/ScriptEngine.HostedScript/ScriptEngine.HostedScript.csproj'
s = p.read_text(encoding='utf-8-sig')
s2 = '\n'.join(l for l in s.splitlines() if 'OneScript.Web.Server.csproj' not in l) + '\n'
if s2 != s:
    p.write_text(s2, encoding='utf-8')
    print('исправлено: убрана ссылка на OneScript.Web.Server')
else:
    print('уже применено: ссылка на OneScript.Web.Server')

# 2. Консоль подписывается на Ctrl+C (POSIX-сигнал). В браузере сигналов нет: PlatformNotSupportedException.
patch('src/OneScript.StandardLibrary/Text/ConsoleContext.cs',
      '            Console.CancelKeyPress += ConsoleOnCancelKeyPress;',
      '''            try
            {
                Console.CancelKeyPress += ConsoleOnCancelKeyPress;
            }
            catch (PlatformNotSupportedException)
            {
                // В браузере (WebAssembly) сигналов ОС нет, обработчик Ctrl+C не нужен
            }''',
      'обработчик Ctrl+C в Консоли')

# 3. Сбор всех синтаксических ошибок (для подсветки в редакторе). При запуске используется
#    ThrowingErrorSink: первая ошибка бросает исключение, поэтому для выполнения кода ничего не меняется.
#    Работает только при проверке с ListErrorSink.
# 3а. После «Ожидается ;» парсер выходил из блока операторов, и остаток модуля не проверялся.
#     Теперь продолжаем разбор с места, куда перешло восстановление (в конце текста — выходим).
patch('src/OneScript.Language/SyntaxAnalysis/DefaultBslParser.cs',
      '''                    if (!endTokens.Contains(_lastExtractedLexem.Token))
                    {
                        AddError(LocalizedErrors.SemicolonExpected());
                    }
                    break;''',
      '''                    if (!endTokens.Contains(_lastExtractedLexem.Token))
                    {
                        // Песочница: если следующий оператор начинается с имени (А = 1 ⏎ Б = 2;),
                        // его не пропускаем, а разбираем и проверяем как обычно
                        AddError(LocalizedErrors.SemicolonExpected(),
                            _lastExtractedLexem.Type != LexemType.Identifier);
                        // Песочница: продолжаем разбор после восстановления, чтобы найти остальные ошибки
                        if (_lastExtractedLexem.Token != Token.EndOfText)
                            continue;
                    }
                    break;''',
      'продолжение разбора после «Ожидается ;»')

# 3б. Восстановление после ошибки пропускало всё до ключевого слова (Если/Для/Пока/Попытка/Перем),
#     поэтому обычные операторы между ними не проверялись. Теперь оно останавливается и на «;»:
#     следующий оператор разбирается и проверяется как обычно. Восстановление всегда читает
#     хотя бы одну лексему, так что зацикливания нет.
patch('src/OneScript.Language/SyntaxAnalysis/NextStatementRecoveryStrategy.cs',
      '''                    if(AdditionalStops != null && AdditionalStops.Contains(lastLexem.Token) )
                    {
                        break;
                    }''',
      '''                    if(AdditionalStops != null && AdditionalStops.Contains(lastLexem.Token) )
                    {
                        break;
                    }
                    // Песочница: «;» завершает оператор, со следующего можно продолжить разбор
                    if (lastLexem.Token == Token.Semicolon)
                    {
                        break;
                    }''',
      'восстановление после ошибки до «;»')

# 3в. Меньше ложных ошибок-каскадов. Раньше после ошибки внутри скобок (Сообщить(А;) восстановление
#     останавливалось только на токенах самого внутреннего блока (закрывающей скобке) и проглатывало
#     КонецЕсли/КонецЦикла внешних блоков. Теперь:
#     - если парсер уже стоит на «;», «Тогда», «Цикл» или на закрывающем слове открытого блока,
#       ничего не пропускаем (например, при ошибке в условии «Если Тогда» не проглатываем «Тогда»);
#     - иначе пропускаем до закрывающего слова любого открытого блока (а не только внутреннего);
#     - вторая ошибка в той же точке всегда сдвигает разбор вперёд, так что зацикливания нет.
patch('src/OneScript.Language/SyntaxAnalysis/DefaultBslParser.cs',
      '''            ErrorSink.AddError(err);

            if (doFastForward)
            {
                if (_tokenStack.Count != 0)
                    SkipToNextStatement(_tokenStack.Peek());
                else
                    SkipToNextStatement();
            }''',
      '''            // Песочница: ошибки, вызванные незакрытой кавычкой в этом же операторе, не записываем
            if (!_sandboxSuppressErrors)
                ErrorSink.AddError(err);

            // Песочница: восстановление с учётом всех открытых блоков
            var errLine = err.Position.LineNumber;
            var errCol = err.Position.ColumnNumber;
            var samePlace = errLine == _sandboxLastErrLine && errCol == _sandboxLastErrCol;
            _sandboxLastErrLine = errLine;
            _sandboxLastErrCol = errCol;
            var current = _lastExtractedLexem.Token;
            var atSafeStop = current == Token.Semicolon || current == Token.Then || current == Token.Loop
                || (LanguageDef.IsEndOfBlockToken(current) && _tokenStack.Any(x => x.Contains(current)));

            if (doFastForward && (samePlace || !atSafeStop))
            {
                if (_tokenStack.Count != 0)
                    SkipToNextStatement(_tokenStack.SelectMany(x => x).Distinct().ToArray());
                else
                    SkipToNextStatement();
            }''',
      'восстановление с учётом всех открытых блоков')

# 3е. При синтаксической ошибке в выражении (Б = ;) парсер вставляет в дерево узел-ошибку, и генератор
#     кода падал на нём, не дойдя до остального модуля: неизвестные переменные ниже не находились.
#     В режиме проверки (флаг ставит Runner.Check) сбой одного оператора не прерывает обход остальных.
patch('src/OneScript.Language/SyntaxAnalysis/BslSyntaxWalker.cs',
      '''            foreach (var statement in statements.Children)
            {
                VisitStatement(statement);
            }''',
      '''            foreach (var statement in statements.Children)
            {
                if (!SandboxCheckMode)
                {
                    VisitStatement(statement);
                    continue;
                }
                // Песочница: при проверке сбой на испорченном операторе не мешает проверить остальные
                try
                {
                    VisitStatement(statement);
                }
                catch (System.Exception)
                {
                }
            }''',
      'обход операторов после сбоя в режиме проверки')

patch('src/OneScript.Language/SyntaxAnalysis/BslSyntaxWalker.cs',
      '''        protected virtual void VisitCodeBlock(CodeBatchNode statements)''',
      '''        /// <summary>Песочница: режим проверки кода без запуска (Runner.Check).</summary>
        public static bool SandboxCheckMode { get; set; }

        protected virtual void VisitCodeBlock(CodeBatchNode statements)''',
      'флаг режима проверки')

# 4. «Некорректный/Незавершённый строковый литерал» ставился на строку ПОСЛЕ незакрытой кавычки:
#    ошибка возникает на символе перевода строки, а итератор в этот момент уже считает следующую строку.
#    Теперь ошибка указывает на начало незакрытого фрагмента строки: открывающую кавычку
#    или «|» последней строки продолжения. Влияет и на запуск, и на проверку.
patch('src/OneScript.Language/LexicalAnalysis/StringLexerState.cs',
      '''            StringBuilder contentBuilder = new StringBuilder();

            while (iterator.MoveNext())''',
      '''            StringBuilder contentBuilder = new StringBuilder();
            // Песочница: начало текущего фрагмента строки (кавычка или «|») — туда указывает ошибка
            var segmentStart = iterator.GetErrorPosition();

            while (iterator.MoveNext())''',
      'позиция начала строкового литерала')

patch('src/OneScript.Language/LexicalAnalysis/StringLexerState.cs',
      '''                    if (iterator.CurrentSymbol != '|')
                        throw CreateExceptionOnCurrentLine("Некорректный строковый литерал", iterator);

                    contentBuilder.Append('\\n');''',
      '''                    if (iterator.CurrentSymbol != '|')
                        throw new SyntaxErrorException(segmentStart, "Незакрытая кавычка в строковом литерале");

                    segmentStart = iterator.GetErrorPosition();
                    contentBuilder.Append('\\n');''',
      'ошибка незакрытой кавычки на строке с кавычкой')

patch('src/OneScript.Language/LexicalAnalysis/StringLexerState.cs',
      '''            throw CreateExceptionOnCurrentLine("Незавершённый строковый литерал", iterator);''',
      '''            // Песочница: текст закончился внутри строки
            throw new SyntaxErrorException(segmentStart, "Незакрытая кавычка в строковом литерале");''',
      'незавершённый литерал в конце текста')

# 5. Ошибки лексера (незакрытая кавычка, недопустимый символ) бросали исключение мимо механизма
#    восстановления, и проверка обрывалась на первой из них. В режиме проверки парсер записывает такую
#    ошибку и продолжает: незакрытая строка считается строковым литералом до конца строки, на прочих
#    ошибках разбор идёт со следующей лексемы. Повтор ошибки в той же точке сдвигает итератор.
patch('src/OneScript.Language/SyntaxAnalysis/DefaultBslParser.cs',
      '''        private void NextLexem()
        {
            _lastExtractedLexem = _lexer.NextLexem();
        }''',
      '''        private void NextLexem()
        {
            if (!BslSyntaxWalker.SandboxCheckMode)
            {
                _lastExtractedLexem = _lexer.NextLexem();
                return;
            }

            // Песочница: в режиме проверки ошибка лексера не обрывает разбор
            while (true)
            {
                try
                {
                    _lastExtractedLexem = _lexer.NextLexem();
                    return;
                }
                catch (SyntaxErrorException e)
                {
                    ErrorSink.AddError(new CodeError
                    {
                        ErrorId = "SyntaxError",
                        Description = e.ErrorDescription,
                        Position = e.GetPosition()
                    });

                    var pos = _lexer.Iterator.Position;
                    if (pos == _sandboxLastLexerErrorPos && !_lexer.Iterator.MoveNext())
                    {
                        _lastExtractedLexem = Lexem.EndOfText();
                        return;
                    }
                    _sandboxLastLexerErrorPos = pos;

                    if (e.ErrorDescription.StartsWith("Незакрытая кавычка"))
                    {
                        // Остаток строки ушёл в литерал: прочие ошибки этого оператора — следствие
                        _sandboxSuppressErrors = true;
                        _lastExtractedLexem = new Lexem { Type = LexemType.StringLiteral, Content = "" };
                        return;
                    }
                }
            }
        }''',
      'продолжение разбора после ошибки лексера')

patch('src/OneScript.Language/SyntaxAnalysis/DefaultBslParser.cs',
      '''        private bool _isInFunctionScope;''',
      '''        private int _sandboxLastLexerErrorPos = -2;
        private bool _sandboxSuppressErrors;
        private bool _isInFunctionScope;''',
      'поля для ошибок лексера')

# Новый оператор — новые ошибки снова записываются
patch('src/OneScript.Language/SyntaxAnalysis/DefaultBslParser.cs',
      '''            while (true)
            {
                if (endTokens.Contains(_lastExtractedLexem.Token))''',
      '''            while (true)
            {
                _sandboxSuppressErrors = false;
                if (endTokens.Contains(_lastExtractedLexem.Token))''',
      'сброс подавления ошибок на новом операторе')

patch('src/OneScript.Language/SyntaxAnalysis/DefaultBslParser.cs',
      '''        private bool _isInFunctionScope;''',
      '''        private bool _isInFunctionScope;
        private int _sandboxLastErrLine = -2;
        private int _sandboxLastErrCol = -2;''',
      'поля для защиты от зацикливания восстановления')
