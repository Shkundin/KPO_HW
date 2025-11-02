using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceApp
{
    // ==========================
    // ===== Domain Model =======
    // ==========================

    public interface IEntity
    {
        Guid Id { get; }
    }

    public enum CategoryType { Income, Expense }
    public enum OperationType { Income, Expense }

    public sealed class BankAccount : IEntity
    {
        public Guid Id { get; init; }
        public string Name { get; private set; }
        public decimal Balance { get; private set; }

        [JsonConstructor]
        public BankAccount(Guid id, string name, decimal balance)
        {
            Id = id;
            Name = name;
            Balance = balance;
        }

        internal void Apply(decimal delta)
        {
            Balance += delta;
        }

        public override string ToString() => $"[{Id}] {Name}: {Balance:C2}";
    }

    public sealed class Category : IEntity
    {
        public Guid Id { get; init; }
        public CategoryType Type { get; private set; }
        public string Name { get; private set; }

        [JsonConstructor]
        public Category(Guid id, CategoryType type, string name)
        {
            Id = id;
            Type = type;
            Name = name;
        }

        public override string ToString() => $"[{Id}] {Name} ({Type})";
    }

    public sealed class Operation : IEntity
    {
        public Guid Id { get; init; }
        public OperationType Type { get; private set; }
        public Guid BankAccountId { get; private set; }
        public decimal Amount { get; private set; }
        public DateTime Date { get; private set; }
        public string? Description { get; private set; }
        public Guid CategoryId { get; private set; }

        [JsonConstructor]
        public Operation(Guid id, OperationType type, Guid bankAccountId, decimal amount, DateTime date, string? description, Guid categoryId)
        {
            Id = id;
            Type = type;
            BankAccountId = bankAccountId;
            Amount = amount;
            Date = date;
            Description = description;
            CategoryId = categoryId;
        }

        public override string ToString() => $"[{Date:yyyy-MM-dd}] {Type} {Amount:C2} (acc={BankAccountId}, cat={CategoryId}) {Description}";
    }

    // ====================================
    // ===== Factory (validation gate) =====
    // ====================================

    public interface IDomainFactory
    {
        BankAccount CreateBankAccount(string name, decimal initialBalance = 0m);
        Category CreateCategory(string name, CategoryType type);
        Operation CreateOperation(OperationType type, Guid bankAccountId, Guid categoryId, decimal amount, DateTime date, string? description = null);
    }

    public sealed class DomainFactory : IDomainFactory
    {
        public BankAccount CreateBankAccount(string name, decimal initialBalance = 0m)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Account name must not be empty.");
            if (initialBalance < 0m) throw new ArgumentException("Initial balance cannot be negative.");
            return new BankAccount(Guid.NewGuid(), name.Trim(), initialBalance);
        }

        public Category CreateCategory(string name, CategoryType type)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Category name must not be empty.");
            return new Category(Guid.NewGuid(), type, name.Trim());
        }

        public Operation CreateOperation(OperationType type, Guid bankAccountId, Guid categoryId, decimal amount, DateTime date, string? description = null)
        {
            if (amount <= 0m) throw new ArgumentException("Operation amount must be positive.");
            return new Operation(Guid.NewGuid(), type, bankAccountId, amount, date, description, categoryId);
        }
    }

    // ========================
    // ===== Repositories =====
    // ========================

    public interface IRepository<T> where T : class, IEntity
    {
        IEnumerable<T> GetAll();
        T? Get(Guid id);
        void Add(T entity);
        void Update(T entity);
        void Remove(Guid id);
    }

    public interface IBankAccountRepository : IRepository<BankAccount> { }
    public interface ICategoryRepository : IRepository<Category> { }
    public interface IOperationRepository : IRepository<Operation>
    {
        IEnumerable<Operation> GetByAccount(Guid accountId);
        IEnumerable<Operation> GetByPeriod(DateTime from, DateTime to);
    }

    
    public interface IStorage
    {
        List<T> Load<T>(string bucket) where T : class;
        void Save<T>(string bucket, List<T> data) where T : class;
    }

    public sealed class JsonFileStorage : IStorage
    {
        private readonly string _root;
        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public JsonFileStorage(string root)
        {
            _root = root;
            Directory.CreateDirectory(_root);
        }

        private string PathFor(string bucket) => System.IO.Path.Combine(_root, $"{bucket}.json");

        public List<T> Load<T>(string bucket) where T : class
        {
            var path = PathFor(bucket);
            if (!File.Exists(path)) return new List<T>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<T>>(json, _opts) ?? new List<T>();
        }

        public void Save<T>(string bucket, List<T> data) where T : class
        {
            var path = PathFor(bucket);
            var json = JsonSerializer.Serialize(data, _opts);
            File.WriteAllText(path, json);
        }
    }

    public abstract class FileRepositoryBase<T> : IRepository<T> where T : class, IEntity
    {
        private readonly IStorage _storage;
        private readonly string _bucket;

        protected FileRepositoryBase(IStorage storage, string bucket)
        {
            _storage = storage;
            _bucket = bucket;
        }

        protected List<T> LoadAll() => _storage.Load<T>(_bucket);
        protected void SaveAll(List<T> list) => _storage.Save(_bucket, list);

        public virtual IEnumerable<T> GetAll() => LoadAll();

        public virtual T? Get(Guid id) => LoadAll().FirstOrDefault(e => e.Id == id);

        public virtual void Add(T entity)
        {
            var list = LoadAll();
            list.Add(entity);
            SaveAll(list);
        }

        public virtual void Update(T entity)
        {
            var list = LoadAll();
            var idx = list.FindIndex(e => e.Id == entity.Id);
            if (idx >= 0) { list[idx] = entity; SaveAll(list); }
        }

        public virtual void Remove(Guid id)
        {
            var list = LoadAll();
            list.RemoveAll(e => e.Id == id);
            SaveAll(list);
        }
    }

    public sealed class BankAccountFileRepository : FileRepositoryBase<BankAccount>, IBankAccountRepository
    { public BankAccountFileRepository(IStorage storage) : base(storage, "accounts") { } }

    public sealed class CategoryFileRepository : FileRepositoryBase<Category>, ICategoryRepository
    { public CategoryFileRepository(IStorage storage) : base(storage, "categories") { } }

    public sealed class OperationFileRepository : FileRepositoryBase<Operation>, IOperationRepository
    {
        public OperationFileRepository(IStorage storage) : base(storage, "operations") { }
        public IEnumerable<Operation> GetByAccount(Guid accountId) => GetAll().Where(o => o.BankAccountId == accountId);
        public IEnumerable<Operation> GetByPeriod(DateTime from, DateTime to) => GetAll().Where(o => o.Date >= from && o.Date <= to);
    }

    
    public class CachingRepositoryProxy<T> : IRepository<T> where T : class, IEntity
    {
        private readonly IRepository<T> _inner;
        private readonly Dictionary<Guid, T> _cache;

        public CachingRepositoryProxy(IRepository<T> inner)
        {
            _inner = inner;
            _cache = _inner.GetAll().ToDictionary(e => e.Id, e => e);
        }

        public IEnumerable<T> GetAll() => _cache.Values.ToList();
        public T? Get(Guid id) => _cache.TryGetValue(id, out var e) ? e : null;

        public void Add(T entity)
        {
            _inner.Add(entity);
            _cache[entity.Id] = entity;
        }

        public void Update(T entity)
        {
            _inner.Update(entity);
            _cache[entity.Id] = entity;
        }

        public void Remove(Guid id)
        {
            _inner.Remove(id);
            _cache.Remove(id);
        }
    }

    public sealed class CachingBankAccountRepositoryProxy : CachingRepositoryProxy<BankAccount>, IBankAccountRepository
    {
        public CachingBankAccountRepositoryProxy(IRepository<BankAccount> inner) : base(inner) { }
    }

    public sealed class CachingCategoryRepositoryProxy : CachingRepositoryProxy<Category>, ICategoryRepository
    {
        public CachingCategoryRepositoryProxy(IRepository<Category> inner) : base(inner) { }
    }

    
    public sealed class CachingOperationRepositoryProxy : IOperationRepository
    {
        private readonly OperationFileRepository _inner;
        private readonly Dictionary<Guid, Operation> _cache;

        public CachingOperationRepositoryProxy(OperationFileRepository inner)
        {
            _inner = inner;
            _cache = _inner.GetAll().ToDictionary(e => e.Id, e => e);
        }

        public IEnumerable<Operation> GetAll() => _cache.Values.ToList();
        public Operation? Get(Guid id) => _cache.TryGetValue(id, out var e) ? e : null;

        public void Add(Operation entity)
        {
            _inner.Add(entity);
            _cache[entity.Id] = entity;
        }

        public void Update(Operation entity)
        {
            _inner.Update(entity);
            _cache[entity.Id] = entity;
        }

        public void Remove(Guid id)
        {
            _inner.Remove(id);
            _cache.Remove(id);
        }

        public IEnumerable<Operation> GetByAccount(Guid accountId) => _cache.Values.Where(o => o.BankAccountId == accountId);
        public IEnumerable<Operation> GetByPeriod(DateTime from, DateTime to) => _cache.Values.Where(o => o.Date >= from && o.Date <= to);
    }

    // ==================
    // ===== Facades =====
    // ==================

    public sealed class BankAccountFacade
    {
        private readonly IDomainFactory _factory;
        private readonly IBankAccountRepository _accounts;

        public BankAccountFacade(IDomainFactory factory, IBankAccountRepository accounts)
        {
            _factory = factory; _accounts = accounts;
        }

        public BankAccount Create(string name, decimal initialBalance = 0m)
        {
            var acc = _factory.CreateBankAccount(name, initialBalance);
            _accounts.Add(acc);
            return acc;
        }

        public IEnumerable<BankAccount> All() => _accounts.GetAll();

        public void Rename(Guid id, string newName)
        {
            newName = (newName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Name must not be empty.");
            var acc = _accounts.Get(id) ?? throw new InvalidOperationException("Account not found");
            var updated = new BankAccount(acc.Id, newName, acc.Balance);
            _accounts.Update(updated);
        }

        public void Delete(Guid id) => _accounts.Remove(id);

        internal void Apply(Guid id, decimal delta)
        {
            var acc = _accounts.Get(id) ?? throw new InvalidOperationException("Account not found");
            var updated = new BankAccount(acc.Id, acc.Name, acc.Balance + delta);
            _accounts.Update(updated);
        }

        public void RecalculateBalancesFromOperations(IEnumerable<Operation> operations)
        {
            
            var totals = new Dictionary<Guid, decimal>();
            foreach (var acc in _accounts.GetAll())
                totals[acc.Id] = 0m;

            foreach (var op in operations.OrderBy(o => o.Date))
            {
                var delta = op.Type == OperationType.Income ? op.Amount : -op.Amount;
                if (!totals.ContainsKey(op.BankAccountId)) totals[op.BankAccountId] = 0m;
                totals[op.BankAccountId] += delta;
            }

            foreach (var acc in _accounts.GetAll())
            {
                var newBalance = totals.TryGetValue(acc.Id, out var t) ? t : 0m;
                var updated = new BankAccount(acc.Id, acc.Name, newBalance);
                _accounts.Update(updated);
            }
        }


        public sealed class CategoryFacade
        {
            private readonly IDomainFactory _factory;
            private readonly ICategoryRepository _categories;

            public CategoryFacade(IDomainFactory factory, ICategoryRepository categories)
            { _factory = factory; _categories = categories; }

            public Category Create(string name, CategoryType type)
            {
                var cat = _factory.CreateCategory(name, type);
                _categories.Add(cat);
                return cat;
            }
            public IEnumerable<Category> All() => _categories.GetAll();

            public void Rename(Guid id, string newName)
            {
                newName = (newName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Name must not be empty.");
                var cat = _categories.Get(id) ?? throw new InvalidOperationException("Category not found");
                var updated = new Category(cat.Id, cat.Type, newName);
                _categories.Update(updated);
            }

            public void Delete(Guid id) => _categories.Remove(id);
        }

        public sealed class OperationFacade
        {
            private readonly IDomainFactory _factory;
            private readonly IOperationRepository _operations;
            private readonly BankAccountFacade _accounts;

            public OperationFacade(IDomainFactory factory, IOperationRepository operations, BankAccountFacade accounts)
            { _factory = factory; _operations = operations; _accounts = accounts; }

            public Operation Create(OperationType type, Guid bankAccountId, Guid categoryId, decimal amount, DateTime date, string? description = null)
            {
                var op = _factory.CreateOperation(type, bankAccountId, categoryId, amount, date, description);
                _operations.Add(op);
                _accounts.Apply(bankAccountId, type == OperationType.Income ? amount : -amount);
                return op;
            }

            public IEnumerable<Operation> All() => _operations.GetAll();
            public IEnumerable<Operation> ByPeriod(DateTime from, DateTime to) => _operations.GetByPeriod(from, to);

            public void Delete(Guid id)
            {
                var op = _operations.Get(id) ?? throw new InvalidOperationException("Operation not found");
                var delta = op.Type == OperationType.Income ? -op.Amount : op.Amount;
                _accounts.Apply(op.BankAccountId, delta);
                _operations.Remove(id);
            }

            public Operation Edit(Guid id, OperationType newType, Guid newBankAccountId, Guid newCategoryId, decimal newAmount, DateTime newDate, string? newDescription = null)
            {
                var existing = _operations.Get(id) ?? throw new InvalidOperationException("Operation not found");
                
                var reverseDelta = existing.Type == OperationType.Income ? -existing.Amount : existing.Amount;
                _accounts.Apply(existing.BankAccountId, reverseDelta);

                
                var updated = new Operation(existing.Id, newType, newBankAccountId, newAmount, newDate, newDescription, newCategoryId);
                _operations.Update(updated);

                
                var applyDelta = newType == OperationType.Income ? newAmount : -newAmount;
                _accounts.Apply(newBankAccountId, applyDelta);

                return updated;
            }
        }

        public sealed class AnalyticsFacade
        {
            private readonly OperationFacade _ops;
            private readonly ICategoryRepository _categories;

            public AnalyticsFacade(OperationFacade ops, ICategoryRepository categories)
            { _ops = ops; _categories = categories; }

            public (decimal income, decimal expense, decimal net) IncomeExpenseNet(DateTime from, DateTime to)
            {
                var list = _ops.ByPeriod(from, to);
                var income = list.Where(o => o.Type == OperationType.Income).Sum(o => o.Amount);
                var expense = list.Where(o => o.Type == OperationType.Expense).Sum(o => o.Amount);
                return (income, expense, income - expense);
            }

            public IEnumerable<(string category, decimal total)> GroupByCategory(DateTime from, DateTime to)
            {
                var list = _ops.ByPeriod(from, to);
                var cats = _categories.GetAll().ToDictionary(c => c.Id, c => c.Name);
                return list.GroupBy(o => o.CategoryId)
                           .Select(g => (category: cats.TryGetValue(g.Key, out var n) ? n : $"{g.Key}", total: g.Sum(x => x.Amount)))
                           .OrderByDescending(x => x.total);
            }
        }

        // ==============================
        // ===== Command + Decorator =====
        // ==============================

        public interface ICommand
        {
            string Name { get; }
            void Execute();
        }

        public interface IProfiler
        {
            void Trace(string message);
        }

        public sealed class ConsoleProfiler : IProfiler
        {
            public void Trace(string message) => Console.WriteLine(message);
        }

        public sealed class TimedCommandDecorator : ICommand
        {
            private readonly ICommand _inner;
            private readonly IProfiler _profiler;
            public string Name => _inner.Name;
            public TimedCommandDecorator(ICommand inner, IProfiler profiler)
            { _inner = inner; _profiler = profiler; }

            public void Execute()
            {
                var sw = Stopwatch.StartNew();
                _inner.Execute();
                sw.Stop();
                _profiler.Trace($"[TIMING] '{Name}' executed in {sw.ElapsedMilliseconds} ms");
            }
        }

        public sealed class AddIncomeCommand : ICommand
        {
            private readonly OperationFacade _ops;
            private readonly Guid _accId, _catId; private readonly decimal _amount; private readonly string? _desc;
            public string Name => "AddIncome";
            public AddIncomeCommand(OperationFacade ops, Guid accId, Guid catId, decimal amount, string? desc)
            { _ops = ops; _accId = accId; _catId = catId; _amount = amount; _desc = desc; }
            public void Execute() => _ops.Create(OperationType.Income, _accId, _catId, _amount, DateTime.Now, _desc);
        }

        public sealed class AddExpenseCommand : ICommand
        {
            private readonly OperationFacade _ops;
            private readonly Guid _accId, _catId; private readonly decimal _amount; private readonly string? _desc;
            public string Name => "AddExpense";
            public AddExpenseCommand(OperationFacade ops, Guid accId, Guid catId, decimal amount, string? desc)
            { _ops = ops; _accId = accId; _catId = catId; _amount = amount; _desc = desc; }
            public void Execute() => _ops.Create(OperationType.Expense, _accId, _catId, _amount, DateTime.Now, _desc);
        }

        public sealed class TransferCommand : ICommand
        {
            private readonly OperationFacade _ops; private readonly Guid _fromAcc, _toAcc, _catIncome, _catExpense; private readonly decimal _amount;
            public string Name => "TransferBetweenAccounts";
            public TransferCommand(OperationFacade ops, Guid fromAcc, Guid toAcc, Guid catIncome, Guid catExpense, decimal amount)
            { _ops = ops; _fromAcc = fromAcc; _toAcc = toAcc; _catIncome = catIncome; _catExpense = catExpense; _amount = amount; }
            public void Execute()
            {
                _ops.Create(OperationType.Expense, _fromAcc, _catExpense, _amount, DateTime.Now, "Transfer out");
                _ops.Create(OperationType.Income, _toAcc, _catIncome, _amount, DateTime.Now, "Transfer in");
            }
        }

        // ===================================
        // ===== Template Method: Import =====
        // ===================================

        public abstract class DataImporter
        {
            protected readonly CategoryFacade Categories;
            protected readonly BankAccountFacade Accounts;
            protected readonly OperationFacade Operations;

            protected DataImporter(CategoryFacade categories, BankAccountFacade accounts, OperationFacade operations)
            { Categories = categories; Accounts = accounts; Operations = operations; }

            public void Import(string path)
            {
                var text = File.ReadAllText(path);
                var models = Parse(text);
                foreach (var m in models)
                {
                    var cat = Categories.All().FirstOrDefault(c => c.Name == m.CategoryName && ((c.Type == CategoryType.Income && m.Type == OperationType.Income) || (c.Type == CategoryType.Expense && m.Type == OperationType.Expense)))
                              ?? Categories.Create(m.CategoryName, m.Type == OperationType.Income ? CategoryType.Income : CategoryType.Expense);

                    var acc = Accounts.All().FirstOrDefault(a => a.Name == m.AccountName) ?? Accounts.Create(m.AccountName, 0);

                    Operations.Create(m.Type, acc.Id, cat.Id, m.Amount, m.Date, m.Description);
                }
            }

            protected abstract List<ImportRow> Parse(string content);

            protected sealed record ImportRow(OperationType Type, string AccountName, string CategoryName, decimal Amount, DateTime Date, string? Description);
        }

        public sealed class CsvImporter : DataImporter
        {
            public CsvImporter(CategoryFacade c, BankAccountFacade a, OperationFacade o) : base(c, a, o) { }
            
            protected override List<ImportRow> Parse(string content)
            {
                var list = new List<ImportRow>();
                foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (line.StartsWith("#")) continue;
                    var parts = line.Split(',');
                    if (parts.Length < 5) continue;
                    var type = Enum.Parse<OperationType>(parts[0], true);
                    var acc = parts[1];
                    var cat = parts[2];
                    var amount = decimal.Parse(parts[3], CultureInfo.InvariantCulture);
                    var date = DateTime.Parse(parts[4], CultureInfo.InvariantCulture);
                    var desc = parts.Length >= 6 ? parts[5] : null;
                    list.Add(new ImportRow(type, acc, cat, amount, date, desc));
                }
                return list;
            }
        }

        public sealed class JsonImporter : DataImporter
        {
            public JsonImporter(CategoryFacade c, BankAccountFacade a, OperationFacade o) : base(c, a, o) { }
            private sealed record Item(string Type, string Account, string Category, decimal Amount, string Date, string? Description);
            protected override List<ImportRow> Parse(string content)
            {
                var items = JsonSerializer.Deserialize<List<Item>>(content) ?? new();
                return items.Select(i => new ImportRow(Enum.Parse<OperationType>(i.Type, true), i.Account, i.Category, i.Amount, DateTime.Parse(i.Date, CultureInfo.InvariantCulture), i.Description)).ToList();
            }
        }

        public sealed class YamlImporter : DataImporter
        {
            public YamlImporter(CategoryFacade c, BankAccountFacade a, OperationFacade o) : base(c, a, o) { }
            
            protected override List<ImportRow> Parse(string content)
            {
                var list = new List<ImportRow>();
                var lines = content.Replace("\r", "").Split('\n');
                string? type = null, account = null, category = null, description = null, date = null; decimal? amount = null;
                void Flush()
                {
                    if (type != null && account != null && category != null && amount.HasValue && date != null)
                    {
                        list.Add(new ImportRow(Enum.Parse<OperationType>(type, true), account, category, amount.Value, DateTime.Parse(date, CultureInfo.InvariantCulture), description));
                    }
                    type = account = category = description = date = null; amount = null;
                }
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("- ")) { Flush(); continue; }
                    var idx = line.IndexOf(":", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var val = line[(idx + 1)..].Trim().Trim('\"');
                    switch (key)
                    {
                        case "type": type = val; break;
                        case "account": account = val; break;
                        case "category": category = val; break;
                        case "amount": amount = decimal.Parse(val, CultureInfo.InvariantCulture); break;
                        case "date": date = val; break;
                        case "description": description = string.IsNullOrEmpty(val) ? null : val; break;
                    }
                }
                Flush();
                return list;
            }
        }

        // ==================================
        // ===== Visitor: Export formats =====
        // ==================================

        public interface IVisitable
        {
            void Accept(IExportVisitor visitor);
        }

        public interface IExportVisitor
        {
            void Visit(FinanceModel model);
            string Result { get; }
        }

        public sealed class FinanceModel : IVisitable
        {
            public IReadOnlyList<BankAccount> Accounts { get; }
            public IReadOnlyList<Category> Categories { get; }
            public IReadOnlyList<Operation> Operations { get; }
            public FinanceModel(IEnumerable<BankAccount> a, IEnumerable<Category> c, IEnumerable<Operation> o)
            { Accounts = a.ToList(); Categories = c.ToList(); Operations = o.ToList(); }
            public void Accept(IExportVisitor visitor) => visitor.Visit(this);
        }

        public sealed class CsvExportVisitor : IExportVisitor
        {
            private readonly StringBuilder _sb = new();
            public string Result => _sb.ToString();
            public void Visit(FinanceModel m)
            {
                _sb.AppendLine("# Accounts: id,name,balance");
                foreach (var a in m.Accounts) _sb.AppendLine($"{a.Id},{Escape(a.Name)},{a.Balance.ToString(CultureInfo.InvariantCulture)}");
                _sb.AppendLine();
                _sb.AppendLine("# Categories: id,type,name");
                foreach (var c in m.Categories) _sb.AppendLine($"{c.Id},{c.Type},{Escape(c.Name)}");
                _sb.AppendLine();
                _sb.AppendLine("# Operations: id,type,accountId,amount,date,description,categoryId");
                foreach (var o in m.Operations) _sb.AppendLine($"{o.Id},{o.Type},{o.BankAccountId},{o.Amount.ToString(CultureInfo.InvariantCulture)},{o.Date:yyyy-MM-dd},{Escape(o.Description)}," + o.CategoryId);
            }
            private static string Escape(string? s) => s == null ? string.Empty : s.Contains(',') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        }

        public sealed class JsonExportVisitor : IExportVisitor
        {
            private string _result = string.Empty;
            public string Result => _result;
            public void Visit(FinanceModel m)
            {
                var obj = new
                {
                    accounts = m.Accounts.Select(a => new { a.Id, a.Name, a.Balance }),
                    categories = m.Categories.Select(c => new { c.Id, c.Type, c.Name }),
                    operations = m.Operations.Select(o => new { o.Id, o.Type, o.BankAccountId, o.Amount, Date = o.Date.ToString("yyyy-MM-dd"), o.Description, o.CategoryId })
                };
                _result = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
            }
        }

        public sealed class YamlExportVisitor : IExportVisitor
        {
            private readonly StringBuilder _sb = new();
            public string Result => _sb.ToString();
            public void Visit(FinanceModel m)
            {
                _sb.AppendLine("accounts:");
                foreach (var a in m.Accounts)
                {
                    _sb.AppendLine("  - id: " + a.Id);
                    _sb.AppendLine("    name: \"" + a.Name.Replace("\"", "\\\"") + "\"");
                    _sb.AppendLine("    balance: " + a.Balance.ToString(CultureInfo.InvariantCulture));
                }
                _sb.AppendLine("categories:");
                foreach (var c in m.Categories)
                {
                    _sb.AppendLine("  - id: " + c.Id);
                    _sb.AppendLine("    type: " + c.Type);
                    _sb.AppendLine("    name: \"" + c.Name.Replace("\"", "\\\"") + "\"");
                }
                _sb.AppendLine("operations:");
                foreach (var o in m.Operations)
                {
                    _sb.AppendLine("  - id: " + o.Id);
                    _sb.AppendLine("    type: " + o.Type);
                    _sb.AppendLine("    bankAccountId: " + o.BankAccountId);
                    _sb.AppendLine("    amount: " + o.Amount.ToString(CultureInfo.InvariantCulture));
                    _sb.AppendLine("    date: " + o.Date.ToString("yyyy-MM-dd"));
                    if (!string.IsNullOrEmpty(o.Description)) _sb.AppendLine("    description: \"" + o.Description!.Replace("\"", "\\\"") + "\"");
                    _sb.AppendLine("    categoryId: " + o.CategoryId);
                }
            }
        }

        public static class ExportService
        {
            public static void ExportAll(IBankAccountRepository a, ICategoryRepository c, IOperationRepository o, IExportVisitor visitor, string outPath)
            {
                var model = new FinanceModel(a.GetAll(), c.GetAll(), o.GetAll());
                model.Accept(visitor);
                File.WriteAllText(outPath, visitor.Result, Encoding.UTF8);
            }
        }

        // ======================
        // ===== UI Utils  =====
        // ======================

        public static class Ui
        {
            public static bool UseRubSuffix { get; set; } = false; 
            public static string Money(decimal v)
                => UseRubSuffix ? v.ToString("N2", CultureInfo.CurrentCulture) + " RUB" : v.ToString("C2", CultureInfo.CurrentCulture);

            public static void PrintAccountsTable(IEnumerable<BankAccount> accounts)
            {
                Console.WriteLine("\n== Accounts ==");
                Console.WriteLine($"{"ID",-36}  {"Название",-20}  {"Баланс",15}");
                Console.WriteLine(new string('-', 78));
                foreach (var a in accounts)
                    Console.WriteLine($"{a.Id}  {a.Name,-20}  {Money(a.Balance),15}");
            }

            public static void PrintOperationsTable(IEnumerable<Operation> ops)
            {
                Console.WriteLine("\n== Operations ==");
                Console.WriteLine($"{"Дата",-10}  {"Тип",-7}  {"Счёт",-6}  {"Категория",-10}  {"Сумма",12}  {"Описание",-30}");
                Console.WriteLine(new string('-', 90));
                foreach (var o in ops.OrderByDescending(x => x.Date))
                {
                    Console.WriteLine($"{o.Date:yyyy-MM-dd}  {o.Type,-7}  {o.BankAccountId.ToString()[..6],-6}  {o.CategoryId.ToString()[..10],-10}  {Money(o.Amount),12}  {o.Description}");
                }
            }

            public static Guid PickAccount(BankAccountFacade accounts)
            {
                var list = accounts.All().ToList();
                if (!list.Any()) throw new InvalidOperationException("Нет счетов.");
                for (int i = 0; i < list.Count; i++) Console.WriteLine($"[{i}] {list[i].Name} ({Money(list[i].Balance)})");
                while (true)
                {
                    Console.Write("Выберите счёт (или q для отмены): ");
                    var s = Console.ReadLine();
                    if (string.Equals(s?.Trim(), "q", StringComparison.OrdinalIgnoreCase))
                        throw new OperationCanceledException("Отменено пользователем");
                    if (int.TryParse(s, out var idx) && idx >= 0 && idx < list.Count)
                        return list[idx].Id;
                    Console.WriteLine($"Введите число от 0 до {list.Count - 1}.");
                }
            }

            public static Guid PickCategory(CategoryFacade cats, CategoryType type)
            {
                var list = cats.All().Where(c => c.Type == type).ToList();
                if (!list.Any()) throw new InvalidOperationException("Нет подходящих категорий.");
                for (int i = 0; i < list.Count; i++) Console.WriteLine($"[{i}] {list[i].Name}");
                while (true)
                {
                    Console.Write("Выберите категорию (или q для отмены): ");
                    var s = Console.ReadLine();
                    if (string.Equals(s?.Trim(), "q", StringComparison.OrdinalIgnoreCase))
                        throw new OperationCanceledException("Отменено пользователем");
                    if (int.TryParse(s, out var idx) && idx >= 0 && idx < list.Count)
                        return list[idx].Id;
                    Console.WriteLine($"Введите число от 0 до {list.Count - 1}.");
                }
            }
        }

        // ======================
        // ===== Application =====
        // ======================

        internal static class Program
        {
            private static ServiceProvider BuildServices()
            {
                var services = new ServiceCollection();

                
                services.AddSingleton<IStorage>(_ => new JsonFileStorage(Path.Combine(AppContext.BaseDirectory, "data")));

                
                services.AddSingleton<BankAccountFileRepository>();
                services.AddSingleton<CategoryFileRepository>();
                services.AddSingleton<OperationFileRepository>();

                
                services.AddSingleton<IBankAccountRepository>(sp => new CachingBankAccountRepositoryProxy(sp.GetRequiredService<BankAccountFileRepository>()));
                services.AddSingleton<ICategoryRepository>(sp => new CachingCategoryRepositoryProxy(sp.GetRequiredService<CategoryFileRepository>()));
                services.AddSingleton<IOperationRepository>(sp => new CachingOperationRepositoryProxy(sp.GetRequiredService<OperationFileRepository>()));

                
                services.AddSingleton<IDomainFactory, DomainFactory>();

               
                services.AddSingleton<BankAccountFacade>();
                services.AddSingleton<CategoryFacade>();
                services.AddSingleton<OperationFacade>();
                services.AddSingleton<AnalyticsFacade>();

               
                services.AddSingleton<IProfiler, ConsoleProfiler>();

                
                services.AddSingleton<CsvImporter>();
                services.AddSingleton<JsonImporter>();
                services.AddSingleton<YamlImporter>();

                return services.BuildServiceProvider();
            }

            private static void Demo(ServiceProvider sp)
            {
                var accounts = sp.GetRequiredService<BankAccountFacade>();
                var categories = sp.GetRequiredService<CategoryFacade>();
                var ops = sp.GetRequiredService<OperationFacade>();
                var analytics = sp.GetRequiredService<AnalyticsFacade>();
                var profiler = sp.GetRequiredService<IProfiler>();

                
                if (!accounts.All().Any())
                {
                    var main = accounts.Create("Основной счет", 10000m);
                    var cash = accounts.Create("Наличные", 2000m);

                    var salary = categories.Create("Зарплата", CategoryType.Income);
                    var cashback = categories.Create("Кэшбэк", CategoryType.Income);
                    var cafe = categories.Create("Кафе", CategoryType.Expense);
                    var health = categories.Create("Здоровье", CategoryType.Expense);

                    var addIncome = new TimedCommandDecorator(new AddIncomeCommand(ops, main.Id, salary.Id, 150000m, "Октябрь"), profiler);
                    var addExpense1 = new TimedCommandDecorator(new AddExpenseCommand(ops, main.Id, cafe.Id, 1200m, "кофе"), profiler);
                    var addExpense2 = new TimedCommandDecorator(new AddExpenseCommand(ops, main.Id, health.Id, 3500m, "стоматолог"), profiler);
                    var transfer = new TimedCommandDecorator(new TransferCommand(ops, main.Id, cash.Id, cashback.Id, cafe.Id, 5000m), profiler);

                    addIncome.Execute();
                    addExpense1.Execute();
                    addExpense2.Execute();
                    transfer.Execute();
                }

                Console.WriteLine("\n== Accounts ==");
                foreach (var a in accounts.All()) Console.WriteLine(a);

                Console.WriteLine("\n== Operations (last 90 days) ==");
                var from = DateTime.Now.AddDays(-90); var to = DateTime.Now;
                foreach (var o in ops.ByPeriod(from, to)) Console.WriteLine(o);

                var (inc, exp, net) = analytics.IncomeExpenseNet(from, to);
                Console.WriteLine($"\nAnalytics (90d): Income={Ui.Money(inc)}, Expense={Ui.Money(exp)}, Net={Ui.Money(net)}");

                Console.WriteLine("\nGroup by category (90d):");
                foreach (var (cat, total) in analytics.GroupByCategory(from, to))
                    Console.WriteLine($"- {cat}: {Ui.Money(total)}");

                Console.WriteLine("\nExporting data to CSV/JSON/YAML...");
                ExportService.ExportAll(
                    sp.GetRequiredService<IBankAccountRepository>(),
                    sp.GetRequiredService<ICategoryRepository>(),
                    sp.GetRequiredService<IOperationRepository>(),
                    new CsvExportVisitor(), Path.Combine(AppContext.BaseDirectory, "data", "export.csv"));
                ExportService.ExportAll(
                    sp.GetRequiredService<IBankAccountRepository>(),
                    sp.GetRequiredService<ICategoryRepository>(),
                    sp.GetRequiredService<IOperationRepository>(),
                    new JsonExportVisitor(), Path.Combine(AppContext.BaseDirectory, "data", "export.json"));
                ExportService.ExportAll(
                    sp.GetRequiredService<IBankAccountRepository>(),
                    sp.GetRequiredService<ICategoryRepository>(),
                    sp.GetRequiredService<IOperationRepository>(),
                    new YamlExportVisitor(), Path.Combine(AppContext.BaseDirectory, "data", "export.yaml"));
                Console.WriteLine("Done. Files saved in ./data");
            }

            // ===== Helpers =====
            private static void GenerateTestData(ServiceProvider sp, int n)
            {
                var accounts = sp.GetRequiredService<BankAccountFacade>().All().ToList();
                var cats = sp.GetRequiredService<CategoryFacade>().All().ToList();
                var ops = sp.GetRequiredService<OperationFacade>();
                var rnd = new Random();
                if (!accounts.Any() || !cats.Any()) return;
                for (int i = 0; i < n; i++)
                {
                    var acc = accounts[rnd.Next(accounts.Count)];
                    var isIncome = rnd.NextDouble() < 0.4; 
                    var type = isIncome ? OperationType.Income : OperationType.Expense;
                    var pool = cats.Where(c => (isIncome && c.Type == CategoryType.Income) || (!isIncome && c.Type == CategoryType.Expense)).ToList();
                    if (!pool.Any()) continue;
                    var cat = pool[rnd.Next(pool.Count)];
                    var amount = (decimal)(Math.Round(rnd.NextDouble() * 5000 + 200, 2));
                    var date = DateTime.Now.AddDays(-rnd.Next(0, 120));
                    var desc = isIncome ? "auto income" : "auto expense";
                    ops.Create(type, acc.Id, cat.Id, amount, date, desc);
                }
            }

            private static void ClearData()
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "data");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
            }

            private static void Menu(ServiceProvider sp)
            {
                var accounts = sp.GetRequiredService<BankAccountFacade>();
                var categories = sp.GetRequiredService<CategoryFacade>();
                var ops = sp.GetRequiredService<OperationFacade>();
                var analytics = sp.GetRequiredService<AnalyticsFacade>();

                while (true)
                {
                    Console.WriteLine("\n=== MENU ===");
                    Console.WriteLine("1) Показать счета");
                    Console.WriteLine("2) Показать операции (30 дней)");
                    Console.WriteLine("3) Добавить доход");
                    Console.WriteLine("4) Добавить расход");
                    Console.WriteLine("5) Перевод между счетами");
                    Console.WriteLine("6) Аналитика за период");
                    Console.WriteLine("7) Экспорт CSV/JSON/YAML");
                    Console.WriteLine("8) Сгенерировать тестовые операции");
                    Console.WriteLine("9) Очистить данные ./data");
                    Console.WriteLine("A) Управление счетами");
                    Console.WriteLine("C) Управление категориями");
                    Console.WriteLine("D) Удалить операцию");
                    Console.WriteLine("E) Редактировать операцию");
                    Console.WriteLine("I) Импорт CSV/JSON/YAML");
                    Console.WriteLine("R) Пересчитать балансы (по операциям)");
                    Console.WriteLine("T) Переключить формат денег (₽/RUB)");
                    Console.WriteLine("0) Выход");
                    Console.Write("Ваш выбор: ");
                    var choice = Console.ReadLine();
                    try
                    {
                        switch (choice?.Trim().ToUpperInvariant())
                        {
                            case "1":
                                Ui.PrintAccountsTable(accounts.All());
                                break;
                            case "2":
                                Ui.PrintOperationsTable(ops.ByPeriod(DateTime.Now.AddDays(-30), DateTime.Now));
                                break;
                            case "3":
                                {
                                    var accI = Ui.PickAccount(accounts);
                                    var catI = Ui.PickCategory(categories, CategoryType.Income);
                                    Console.Write("Сумма: "); var ai = decimal.Parse(Console.ReadLine()!, CultureInfo.CurrentCulture);
                                    Console.Write("Описание: "); var di = Console.ReadLine();
                                    ops.Create(OperationType.Income, accI, catI, ai, DateTime.Now, di);
                                    Console.WriteLine("Доход добавлен.");
                                }
                                break;
                            case "4":
                                {
                                    var accE = Ui.PickAccount(accounts);
                                    var catE = Ui.PickCategory(categories, CategoryType.Expense);
                                    Console.Write("Сумма: "); var ae = decimal.Parse(Console.ReadLine()!, CultureInfo.CurrentCulture);
                                    Console.Write("Описание: "); var de = Console.ReadLine();
                                    ops.Create(OperationType.Expense, accE, catE, ae, DateTime.Now, de);
                                    Console.WriteLine("Расход добавлен.");
                                }
                                break;
                            case "5":
                                {
                                    Console.WriteLine("Счёт-источник:"); var fromAcc = Ui.PickAccount(accounts);
                                    Console.WriteLine("Счёт-получатель:"); var toAcc = Ui.PickAccount(accounts);

                                    var incomeCat = categories.All().FirstOrDefault(c => c.Type == CategoryType.Income)
                                                    ?? categories.Create("Transfer In", CategoryType.Income);
                                    var expenseCat = categories.All().FirstOrDefault(c => c.Type == CategoryType.Expense)
                                                     ?? categories.Create("Transfer Out", CategoryType.Expense);

                                    Console.Write("Сумма: "); var amt = decimal.Parse(Console.ReadLine()!, CultureInfo.CurrentCulture);
                                    ops.Create(OperationType.Expense, fromAcc, expenseCat.Id, amt, DateTime.Now, "Transfer out");
                                    ops.Create(OperationType.Income, toAcc, incomeCat.Id, amt, DateTime.Now, "Transfer in");
                                    Console.WriteLine("Перевод выполнен.");
                                }
                                break;
                            case "6":
                                {
                                    Console.Write("От (yyyy-MM-dd): "); var s = DateTime.Parse(Console.ReadLine()!, CultureInfo.InvariantCulture);
                                    Console.Write("До (yyyy-MM-dd): "); var e = DateTime.Parse(Console.ReadLine()!, CultureInfo.InvariantCulture);
                                    var (inc, exp, net) = analytics.IncomeExpenseNet(s, e);
                                    Console.WriteLine($"Доход={Ui.Money(inc)}, Расход={Ui.Money(exp)}, Нетто={Ui.Money(net)}");
                                    Console.WriteLine("\nГруппы по категориям:");
                                    foreach (var (cat, total) in analytics.GroupByCategory(s, e))
                                        Console.WriteLine($"- {cat}: {Ui.Money(total)}");
                                }
                                break;
                            case "7":
                                Console.WriteLine("Экспорт...");
                                ExportService.ExportAll(
                                    sp.GetRequiredService<IBankAccountRepository>(),
                                    sp.GetRequiredService<ICategoryRepository>(),
                                    sp.GetRequiredService<IOperationRepository>(),
                                    new CsvExportVisitor(), Path.Combine(AppContext.BaseDirectory, "data", "export.csv"));
                                ExportService.ExportAll(
                                    sp.GetRequiredService<IBankAccountRepository>(),
                                    sp.GetRequiredService<ICategoryRepository>(),
                                    sp.GetRequiredService<IOperationRepository>(),
                                    new JsonExportVisitor(), Path.Combine(AppContext.BaseDirectory, "data", "export.json"));
                                ExportService.ExportAll(
                                    sp.GetRequiredService<IBankAccountRepository>(),
                                    sp.GetRequiredService<ICategoryRepository>(),
                                    sp.GetRequiredService<IOperationRepository>(),
                                    new YamlExportVisitor(), Path.Combine(AppContext.BaseDirectory, "data", "export.yaml"));
                                Console.WriteLine("Готово: ./data/export.*");
                                break;
                            case "8":
                                Console.Write("Сколько сгенерировать операций?: ");
                                var n = int.Parse(Console.ReadLine()!);
                                GenerateTestData(sp, n);
                                Console.WriteLine($"Добавлено {n} операций.");
                                break;
                            case "9":
                                ClearData();
                                Console.WriteLine("Данные очищены. Перезапустите приложение для чистого состояния.");
                                break;
                            case "A": 
                                {
                                    Console.WriteLine("\n-- Счета --");
                                    Console.WriteLine("1) Создать");
                                    Console.WriteLine("2) Переименовать");
                                    Console.WriteLine("3) Удалить");
                                    Console.Write("Выбор: "); var s = Console.ReadLine();
                                    if (s == "1")
                                    {
                                        Console.Write("Название: "); var name = Console.ReadLine();
                                        Console.Write("Начальный баланс (пусто=0): "); var t = Console.ReadLine();
                                        var init = 0m; if (!string.IsNullOrWhiteSpace(t)) decimal.TryParse(t, NumberStyles.Number, CultureInfo.CurrentCulture, out init);
                                        var acc = accounts.Create(name ?? string.Empty, init);
                                        Console.WriteLine($"Создан счёт: {acc.Name} [{acc.Id}]");
                                    }
                                    else if (s == "2")
                                    {
                                        var id = Ui.PickAccount(accounts);
                                        Console.Write("Новое имя: "); var newName = Console.ReadLine();
                                        accounts.Rename(id, newName ?? string.Empty);
                                        Console.WriteLine("Переименовано.");
                                    }
                                    else if (s == "3")
                                    {
                                        var id = Ui.PickAccount(accounts);
                                        accounts.Delete(id);
                                        Console.WriteLine("Счёт удалён.");
                                    }
                                }
                                break;
                            case "C": 
                                {
                                    Console.WriteLine("\n-- Категории --");
                                    Console.WriteLine("1) Создать");
                                    Console.WriteLine("2) Переименовать");
                                    Console.WriteLine("3) Удалить");
                                    Console.Write("Выбор: "); var s = Console.ReadLine();
                                    if (s == "1")
                                    {
                                        Console.Write("Название: "); var name = Console.ReadLine();
                                        Console.Write("Тип (Income/Expense): "); var typeStr = Console.ReadLine();
                                        if (!Enum.TryParse<CategoryType>(typeStr, true, out var type)) { Console.WriteLine("Неверный тип."); break; }
                                        var cat = categories.Create(name ?? string.Empty, type);
                                        Console.WriteLine($"Создана категория: {cat.Name} ({cat.Type}) [{cat.Id}]");
                                    }
                                    else if (s == "2")
                                    {
                                        Console.Write("Тип категории (Income/Expense): ");
                                        if (!Enum.TryParse<CategoryType>(Console.ReadLine(), true, out var t)) { Console.WriteLine("Неверный тип."); break; }
                                        var id = Ui.PickCategory(categories, t);
                                        Console.Write("Новое имя: "); var newName = Console.ReadLine();
                                        categories.Rename(id, newName ?? string.Empty);
                                        Console.WriteLine("Переименовано.");
                                    }
                                    else if (s == "3")
                                    {
                                        Console.Write("Тип категории (Income/Expense): ");
                                        if (!Enum.TryParse<CategoryType>(Console.ReadLine(), true, out var t)) { Console.WriteLine("Неверный тип."); break; }
                                        var id = Ui.PickCategory(categories, t);
                                        categories.Delete(id);
                                        Console.WriteLine("Категория удалена.");
                                    }
                                }
                                break;
                            case "D": 
                                {
                                    var recent = ops.ByPeriod(DateTime.Now.AddDays(-90), DateTime.Now).ToList();
                                    if (recent.Count == 0) { Console.WriteLine("Нет операций за последние 90 дней."); break; }
                                    for (int i = 0; i < recent.Count; i++) Console.WriteLine($"{i + 1}) {recent[i]}");
                                    Console.Write("№ операции для удаления: ");
                                    if (!int.TryParse(Console.ReadLine(), out var idx) || idx < 1 || idx > recent.Count) { Console.WriteLine("Неверный номер."); break; }
                                    ops.Delete(recent[idx - 1].Id);
                                    Console.WriteLine("Операция удалена.");
                                }
                                break;
                            case "E":
                                {
                                    var recent = ops.ByPeriod(DateTime.Now.AddDays(-30), DateTime.Now).ToList();
                                    if (recent.Count == 0) { Console.WriteLine("Нет операций за последние 30 дней."); break; }
                                    for (int i = 0; i < recent.Count; i++) Console.WriteLine($"{i + 1}) {recent[i]}");
                                    Console.Write("№ операции для редактирования: ");
                                    if (!int.TryParse(Console.ReadLine(), out var idx) || idx < 1 || idx > recent.Count) { Console.WriteLine("Неверный номер."); break; }
                                    var op = recent[idx - 1];

                                    Console.WriteLine("Новая категория (оставьте пустым, чтобы не менять):");
                                    Guid newCatId = op.CategoryId;
                                    var changeCat = Console.ReadLine();
                                    if (!string.IsNullOrWhiteSpace(changeCat))
                                        newCatId = Ui.PickCategory(categories, op.Type == OperationType.Income ? CategoryType.Income : CategoryType.Expense);

                                    Console.Write("Новая сумма (пусто = без изменений): ");
                                    var amountText = Console.ReadLine();
                                    decimal newAmount = op.Amount;
                                    if (!string.IsNullOrWhiteSpace(amountText)) decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.CurrentCulture, out newAmount);

                                    Console.Write("Новая дата (yyyy-MM-dd, пусто = без изменений): ");
                                    var dateText = Console.ReadLine();
                                    DateTime newDate = op.Date;
                                    if (!string.IsNullOrWhiteSpace(dateText)) DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out newDate);

                                    Console.Write("Новое описание (пусто = без изменений): ");
                                    var newDesc = Console.ReadLine();
                                    if (string.IsNullOrWhiteSpace(newDesc)) newDesc = op.Description;

                                    ops.Edit(op.Id, op.Type, op.BankAccountId, newCatId, newAmount, newDate, newDesc);
                                    Console.WriteLine("Операция обновлена.");
                                }
                                break;
                            case "I": 
                                {
                                    Console.Write("Формат (csv/json/yaml): ");
                                    var fmt = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                                    Console.Write("Путь к файлу: ");
                                    var path = Console.ReadLine();
                                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                                    {
                                        Console.WriteLine("Файл не найден.");
                                        break;
                                    }
                                    switch (fmt)
                                    {
                                        case "csv": sp.GetRequiredService<CsvImporter>().Import(path!); break;
                                        case "json": sp.GetRequiredService<JsonImporter>().Import(path!); break;
                                        case "yaml": sp.GetRequiredService<YamlImporter>().Import(path!); break;
                                        default: Console.WriteLine("Неизвестный формат."); break;
                                    }
                                    Console.WriteLine("Импорт завершён.");
                                }
                                break;
                            case "R":
                                {
                                    accounts.RecalculateBalancesFromOperations(ops.All());
                                    Console.WriteLine("Балансы пересчитаны из операций. ВНИМАНИЕ: начальные суммы счетов не учитываются.");
                                }
                                break;
                            case "T":
                                Ui.UseRubSuffix = !Ui.UseRubSuffix;
                                Console.WriteLine($"Формат: {(Ui.UseRubSuffix ? "N2 RUB" : "₽")}");
                                break;
                            case "0":
                                return;
                            default:
                                Console.WriteLine("Неизвестный пункт.");
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("Действие отменено.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Ошибка: " + ex.Message);
                    }
                }
            }

            public static void Main()
            {
                
                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;
                var ru = new CultureInfo("ru-RU");
                CultureInfo.DefaultThreadCurrentCulture = ru;
                CultureInfo.DefaultThreadCurrentUICulture = ru;

                var sp = BuildServices();
                Demo(sp);
                Menu(sp);
                Console.WriteLine("\nPress ENTER to exit...");
                Console.ReadLine();
            }
        }
    }
}
