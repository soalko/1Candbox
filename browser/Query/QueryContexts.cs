// Язык запросов: классы встроенного языка — Запрос, РезультатЗапроса, ВыборкаИзРезультатаЗапроса,
// КолонкиРезультатаЗапроса, МенеджерВременныхТаблиц и перечисление ОбходРезультатаЗапроса.
// Регистрируются в Runner.CreateEngine через env.AddAssembly(typeof(Runner).Assembly).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OneScript.Contexts;
using OneScript.Contexts.Enums;
using OneScript.Exceptions;
using OneScript.Execution;
using OneScript.StandardLibrary.Collections;
using OneScript.StandardLibrary.Collections.ValueTable;
using OneScript.StandardLibrary.TypeDescriptions;
using OneScript.Types;
using OneScript.Values;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

namespace OneScriptWasm.Query
{
    [EnumerationType("ОбходРезультатаЗапроса", "QueryResultIteration")]
    public enum QueryResultIteration
    {
        [EnumValue("Прямой", "Linear")] Linear,
        [EnumValue("ПоГруппировкам", "ByGroups")] ByGroups,
        [EnumValue("ПоГруппировкамСИерархией", "ByGroupsWithHierarchy")] ByGroupsWithHierarchy,
    }

    internal static class QueryErrors
    {
        /// <summary>
        /// Ошибка запроса в формате платформы:
        /// «Ошибка при вызове метода контекста (Выполнить): {(2, 9)}: Поле не найдено "Т.Х"» и строка запроса с маркером &lt;&lt;?&gt;&gt;.
        /// </summary>
        public static RuntimeException Format(string method, string text, QueryException e)
        {
            text ??= "";
            int pos = Math.Clamp(e.Pos, 0, text.Length);
            int line = 1, lineStart = 0;
            for (int i = 0; i < pos; i++)
                if (text[i] == '\n') { line++; lineStart = i + 1; }
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            var src = text.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');
            int col = pos - lineStart;
            var fragment = (src.Substring(0, Math.Min(col, src.Length)) + "<<?>>" + (col < src.Length ? src.Substring(col) : "")).Trim();
            var sb = new StringBuilder();
            sb.Append($"Ошибка при вызове метода контекста ({method}): {{({line}, {col + 1})}}: {e.Message}");
            if (fragment != "<<?>>") sb.Append('\n').Append(fragment);
            return new RuntimeException(sb.ToString());
        }
    }

    /// <summary>Временные таблицы: словарь «имя без учёта регистра → таблица».</summary>
    internal sealed class TempStore
    {
        public readonly Dictionary<string, QueryTable> Tables = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Order = new();
    }

    [ContextClass("Запрос", "Query")]
    public sealed class QueryContext : AutoContext<QueryContext>
    {
        public QueryContext(string text)
        {
            Text = text ?? "";
            Parameters = new StructureImpl();
        }

        [ContextProperty("Текст", "Text")]
        public string Text { get; set; }

        [ContextProperty("Параметры", "Parameters", CanWrite = false)]
        public StructureImpl Parameters { get; }

        [ContextProperty("МенеджерВременныхТаблиц", "TempTablesManager")]
        public TempTablesManager TempTablesManager { get; set; }

        [ContextMethod("УстановитьПараметр", "SetParameter")]
        public void SetParameter(string name, IValue value)
        {
            Parameters.Insert(name, value);
        }

        [ContextMethod("Выполнить", "Execute")]
        public QueryResultContext Execute()
        {
            var results = Run("Выполнить", intermediate: false);
            var last = results[^1];
            return new QueryResultContext(last?.Table ?? new QueryTable());
        }

        [ContextMethod("ВыполнитьПакет", "ExecuteBatch")]
        public ArrayImpl ExecuteBatch() => ToArray(Run("ВыполнитьПакет", intermediate: false), intermediate: false);

        [ContextMethod("ВыполнитьПакетСПромежуточнымиДанными", "ExecuteBatchWithIntermediateData")]
        public ArrayImpl ExecuteBatchWithIntermediateData() =>
            ToArray(Run("ВыполнитьПакетСПромежуточнымиДанными", intermediate: true), intermediate: true);

        private static ArrayImpl ToArray(List<QueryRun.StatementResult> results, bool intermediate)
        {
            var arr = new ArrayImpl();
            foreach (var r in results)
            {
                if (r == null) arr.Add(BslUndefinedValue.Instance);
                else arr.Add(new QueryResultContext(intermediate && r.Placed != null ? r.Placed : r.Table));
            }
            return arr;
        }

        private List<QueryRun.StatementResult> Run(string method, bool intermediate)
        {
            if (TempTablesManager != null && TempTablesManager.Closed)
                throw new RuntimeException("Попытка использования закрытого менеджера временных таблиц");
            // Без менеджера временные таблицы живут только во время одного выполнения пакета
            var store = TempTablesManager?.Store ?? new TempStore();
            try
            {
                var batch = QueryParser.ParseBatch(Text);
                var run = new QueryRun(GetParameter, store.Tables, QueryProviders.All);
                var before = new HashSet<string>(store.Tables.Keys, StringComparer.OrdinalIgnoreCase);
                var results = run.ExecuteBatch(batch);
                store.Order.RemoveAll(n => !store.Tables.ContainsKey(n));
                foreach (var name in store.Tables.Keys)
                    if (!before.Contains(name) && !store.Order.Contains(name, StringComparer.OrdinalIgnoreCase)) store.Order.Add(name);
                return results;
            }
            catch (QueryException e)
            {
                throw QueryErrors.Format(method, Text, e);
            }
            catch (RuntimeException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new RuntimeException($"Ошибка при вызове метода контекста ({method}): внутренняя ошибка выполнения запроса: {e.Message}");
            }
        }

        private BslValue GetParameter(string name)
        {
            if (!Parameters.HasProperty(name)) return null;
            var n = Parameters.GetPropertyNumber(name);
            return QV.Raw(Parameters.GetPropValue(n));
        }

        [ScriptConstructor]
        public static QueryContext Constructor(IValue text = null)
        {
            var raw = text == null ? null : QV.Raw(text);
            if (raw is TempTablesManager)
                throw new RuntimeException("Неверный тип аргумента: передайте текст запроса, а менеджер временных таблиц установите свойством МенеджерВременныхТаблиц");
            return new QueryContext(raw == null || raw is BslUndefinedValue ? "" : raw.ToString());
        }
    }

    /// <summary>Подключаемые источники таблиц (демобаза). Пока пусто.</summary>
    public static class QueryProviders
    {
        public static readonly List<IQueryTableProvider> All = new();
    }

    [ContextClass("РезультатЗапроса", "QueryResult")]
    public sealed class QueryResultContext : AutoContext<QueryResultContext>
    {
        internal QueryTable Data { get; }

        internal QueryResultContext(QueryTable data)
        {
            Data = data;
            Columns = new QueryResultColumns(data);
        }

        [ContextProperty("Колонки", "Columns", CanWrite = false)]
        public QueryResultColumns Columns { get; }

        [ContextMethod("Пустой", "IsEmpty")]
        public bool IsEmpty() => Data.Rows.Count == 0;

        [ContextMethod("Выгрузить", "Unload")]
        public ValueTable Unload(QueryResultIteration? iteration = null)
        {
            if (iteration is QueryResultIteration.ByGroups or QueryResultIteration.ByGroupsWithHierarchy)
                throw new RuntimeException("Обход по группировкам возможен только для запроса с ИТОГИ, а ИТОГИ в песочнице не поддерживаются");
            var vt = new ValueTable();
            var cols = new List<ValueTableColumn>();
            for (int i = 0; i < Data.Columns.Count; i++)
                cols.Add(vt.Columns.AddUnchecked(Data.Columns[i], Data.Columns[i], ColumnType(Data, i)));
            foreach (var row in Data.Rows)
            {
                var r = vt.Add();
                for (int i = 0; i < cols.Count; i++) r.Set(cols[i], row[i]);
            }
            return vt;
        }

        [ContextMethod("Выбрать", "Select")]
        public QueryResultSelection Select(QueryResultIteration? iteration = null, IValue groupings = null)
        {
            if (iteration is QueryResultIteration.ByGroups or QueryResultIteration.ByGroupsWithHierarchy)
                throw new RuntimeException("Обход по группировкам возможен только для запроса с ИТОГИ, а ИТОГИ в песочнице не поддерживаются");
            return new QueryResultSelection(this);
        }

        /// <summary>
        /// Тип колонки по значениям: Число/Строка/Дата/Булево и Null, если он встречается.
        /// Колонка с Неопределено или объектами остаётся без типа (типизированная колонка превратила бы Null в 0).
        /// </summary>
        internal static TypeDescription ColumnType(QueryTable data, int col)
        {
            var types = new List<TypeDescriptor>();
            foreach (var row in data.Rows)
            {
                TypeDescriptor t = row[col] switch
                {
                    BslNumericValue => BasicTypes.Number,
                    BslStringValue => BasicTypes.String,
                    BslDateValue => BasicTypes.Date,
                    BslBooleanValue => BasicTypes.Boolean,
                    BslNullValue => BasicTypes.Null,
                    _ => default,
                };
                if (t == default) return new TypeDescription();
                if (!types.Contains(t)) types.Add(t);
            }
            return new TypeDescription(types.Select(t => new BslTypeValue(t)));
        }
    }

    [ContextClass("КолонкаРезультатаЗапроса", "QueryResultColumn")]
    public sealed class QueryResultColumn : AutoContext<QueryResultColumn>
    {
        internal QueryResultColumn(string name, TypeDescription type)
        {
            Name = name;
            ValueType = type;
        }

        [ContextProperty("Имя", "Name", CanWrite = false)]
        public string Name { get; }

        [ContextProperty("ТипЗначения", "ValueType", CanWrite = false)]
        public TypeDescription ValueType { get; }

        [ContextProperty("Ширина", "Width", CanWrite = false)]
        public int Width => 0;
    }

    [ContextClass("КолонкиРезультатаЗапроса", "QueryResultColumns")]
    public sealed class QueryResultColumns : AutoContext<QueryResultColumns>, ICollectionContext<QueryResultColumn>
    {
        private readonly List<QueryResultColumn> _columns = new();

        internal QueryResultColumns(QueryTable data)
        {
            for (int i = 0; i < data.Columns.Count; i++)
                _columns.Add(new QueryResultColumn(data.Columns[i], QueryResultContext.ColumnType(data, i)));
        }

        [ContextMethod("Количество", "Count")]
        public int Count() => _columns.Count;

        public int Count(IBslProcess process) => Count();

        [ContextMethod("Найти", "Find")]
        public IValue Find(string name) =>
            (IValue)_columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? BslUndefinedValue.Instance;

        [ContextMethod("Получить", "Get")]
        public QueryResultColumn Get(int index)
        {
            if (index < 0 || index >= _columns.Count) throw RuntimeException.IndexOutOfRange();
            return _columns[index];
        }

        public override bool IsIndexed => true;

        public override IValue GetIndexedValue(IValue index)
        {
            var raw = QV.Raw(index);
            if (raw is BslNumericValue n) return Get((int)(decimal)n);
            var found = Find(raw.ToString());
            if (found is BslUndefinedValue) throw RuntimeException.InvalidArgumentValue();
            return found;
        }

        public IEnumerator<QueryResultColumn> GetEnumerator() => _columns.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [ContextClass("ВыборкаИзРезультатаЗапроса", "QueryResultSelection")]
    public sealed class QueryResultSelection : AutoContext<QueryResultSelection>
    {
        private readonly QueryResultContext _owner;
        private readonly QueryTable _data;
        private int _pos = -1;

        internal QueryResultSelection(QueryResultContext owner)
        {
            _owner = owner;
            _data = owner.Data;
        }

        [ContextMethod("Следующий", "Next")]
        public bool Next()
        {
            if (_pos < _data.Rows.Count) _pos++;
            return _pos < _data.Rows.Count;
        }

        [ContextMethod("Количество", "Count")]
        public int Count() => _data.Rows.Count;

        [ContextMethod("Сбросить", "Reset")]
        public void Reset() => _pos = -1;

        [ContextMethod("Владелец", "Owner")]
        public QueryResultContext Owner() => _owner;

        [ContextMethod("Уровень", "Level")]
        public int Level() => 0;

        [ContextMethod("Получить", "Get")]
        public IValue Get(int index)
        {
            if (index < 0 || index >= _data.Columns.Count) throw RuntimeException.IndexOutOfRange();
            return Current[index];
        }

        /// <summary>НайтиСледующий(Структура) или НайтиСледующий(Значение, "Колонка1, Колонка2").</summary>
        [ContextMethod("НайтиСледующий", "FindNext")]
        public bool FindNext(IValue values, string columnNames = null)
        {
            var raw = QV.Raw(values);
            var conditions = new List<(int Col, BslValue Value)>();
            if (raw is StructureImpl s)
            {
                foreach (KeyAndValueImpl kv in s)
                    conditions.Add((ColumnIndex(kv.Key.ToString()), QV.Raw(kv.Value)));
            }
            else
            {
                var names = (columnNames ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (names.Length == 0) throw new RuntimeException("Не указаны имена колонок для поиска");
                foreach (var n in names) conditions.Add((ColumnIndex(n), raw));
            }
            while (Next())
            {
                var row = _data.Rows[_pos];
                if (conditions.All(c => QV.KeyEquals(row[c.Col], c.Value))) return true;
            }
            return false;
        }

        /// <summary>Переходит к следующей записи с другим значением поля.</summary>
        [ContextMethod("СледующийПоЗначениюПоля", "NextByFieldValue")]
        public bool NextByFieldValue(string fieldName)
        {
            int col = ColumnIndex(fieldName);
            if (_pos < 0) return Next();
            var current = _data.Rows[Math.Min(_pos, _data.Rows.Count - 1)][col];
            while (Next())
                if (!QV.KeyEquals(_data.Rows[_pos][col], current)) return true;
            return false;
        }

        private int ColumnIndex(string name)
        {
            int i = _data.Columns.FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw PropertyAccessException.PropNotFoundException(name);
            return i;
        }

        private BslValue[] Current
        {
            get
            {
                if (_pos < 0 || _pos >= _data.Rows.Count)
                    throw new RuntimeException("Выборка не спозиционирована на запись: сначала вызовите Следующий()");
                return _data.Rows[_pos];
            }
        }

        // Свойства выборки — колонки результата
        public override int GetPropCount() => _data.Columns.Count;
        public override string GetPropName(int propNum) => _data.Columns[propNum];
        public override int GetPropertyNumber(string name) => ColumnIndex(name);
        public override bool IsPropReadable(int propNum) => true;
        public override bool IsPropWritable(int propNum) => false;
        public override IValue GetPropValue(int propNum) => Current[propNum];

        public override bool IsIndexed => true;

        public override IValue GetIndexedValue(IValue index)
        {
            var raw = QV.Raw(index);
            if (raw is BslNumericValue n) return Get((int)(decimal)n);
            return Current[ColumnIndex(raw.ToString())];
        }
    }

    [ContextClass("МенеджерВременныхТаблиц", "TempTablesManager")]
    public sealed class TempTablesManager : AutoContext<TempTablesManager>
    {
        internal TempStore Store { get; } = new();
        internal bool Closed { get; private set; }

        public TempTablesManager()
        {
            Tables = new TempTablesCollection(this);
        }

        [ContextProperty("Таблицы", "Tables", CanWrite = false)]
        public TempTablesCollection Tables { get; }

        [ContextMethod("Закрыть", "Close")]
        public void Close()
        {
            Store.Tables.Clear();
            Store.Order.Clear();
            Closed = true;
        }

        [ScriptConstructor]
        public static TempTablesManager Constructor() => new();
    }

    [ContextClass("ТаблицыМенеджераВременныхТаблиц", "TempTablesManagerTables")]
    public sealed class TempTablesCollection : AutoContext<TempTablesCollection>, ICollectionContext<TempTableItem>
    {
        private readonly TempTablesManager _manager;

        internal TempTablesCollection(TempTablesManager manager) { _manager = manager; }

        private List<TempTableItem> Items() =>
            _manager.Store.Order.Where(n => _manager.Store.Tables.ContainsKey(n))
                .Select(n => new TempTableItem(n, _manager.Store.Tables[n])).ToList();

        [ContextMethod("Количество", "Count")]
        public int Count() => _manager.Store.Tables.Count;

        public int Count(IBslProcess process) => Count();

        [ContextMethod("Найти", "Find")]
        public IValue Find(string name)
        {
            if (!_manager.Store.Tables.TryGetValue(name, out var t)) return BslUndefinedValue.Instance;
            var real = _manager.Store.Order.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
            return new TempTableItem(real, t);
        }

        [ContextMethod("Получить", "Get")]
        public TempTableItem Get(int index)
        {
            var items = Items();
            if (index < 0 || index >= items.Count) throw RuntimeException.IndexOutOfRange();
            return items[index];
        }

        public override bool IsIndexed => true;

        public override IValue GetIndexedValue(IValue index)
        {
            var raw = QV.Raw(index);
            if (raw is BslNumericValue n) return Get((int)(decimal)n);
            var found = Find(raw.ToString());
            if (found is BslUndefinedValue) throw new RuntimeException($"Временная таблица не найдена \"{raw}\"");
            return found;
        }

        public IEnumerator<TempTableItem> GetEnumerator() => Items().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [ContextClass("ТаблицаМенеджераВременныхТаблиц", "TempTablesManagerTable")]
    public sealed class TempTableItem : AutoContext<TempTableItem>
    {
        private readonly QueryTable _data;

        internal TempTableItem(string name, QueryTable data)
        {
            FullName = name;
            _data = data;
            Columns = new QueryResultColumns(data);
        }

        [ContextProperty("ПолноеИмя", "FullName", CanWrite = false)]
        public string FullName { get; }

        [ContextProperty("Колонки", "Columns", CanWrite = false)]
        public QueryResultColumns Columns { get; }

        [ContextMethod("ПолучитьДанные", "GetData")]
        public QueryResultContext GetData() => new(_data);
    }
}
