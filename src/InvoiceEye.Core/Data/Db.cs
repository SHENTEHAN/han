using System.Globalization;
using Microsoft.Data.Sqlite;

namespace InvoiceEye.Core.Data;

/// <summary>SQLite 連線與資料表建立。資料庫是一個檔案（預設 data/invoiceeye.db）。</summary>
public sealed class Db
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;

    public Db(string connectionString, bool keepAlive = false)
    {
        _connectionString = connectionString;
        if (keepAlive) _keepAlive = Open(); // 記憶體資料庫要有一條連線一直開著，資料才不會消失
        using var c = Open();
        Exec(c, Schema);
    }

    public static Db ForFile(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new Db(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
    }

    /// <summary>測試用：共用的記憶體資料庫（name 相同的連線共用同一份資料）。</summary>
    public static Db InMemory(string name) =>
        new($"Data Source={name};Mode=Memory;Cache=Shared;Foreign Keys=True", keepAlive: true);

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public static int Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var cmd = Command(c, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static SqliteCommand Command(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, ToDb(value));
        return cmd;
    }

    private static object ToDb(object? v) => v switch
    {
        null => DBNull.Value,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? 1 : 0,
        decimal m => (double)m,
        _ => v,
    };

    public static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Str(SqliteDataReader r, string col) => r[col] is DBNull ? null : (string)r[col];
    public static decimal Dec(SqliteDataReader r, string col) => Convert.ToDecimal(r[col], CultureInfo.InvariantCulture);
    public static decimal? DecN(SqliteDataReader r, string col) => r[col] is DBNull ? null : Dec(r, col);
    public static long Long(SqliteDataReader r, string col) => Convert.ToInt64(r[col], CultureInfo.InvariantCulture);
    public static long? LongN(SqliteDataReader r, string col) => r[col] is DBNull ? null : Long(r, col);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS categories (
            name TEXT PRIMARY KEY,
            budget REAL NULL,
            sort INTEGER NOT NULL DEFAULT 100,
            is_income INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS invoices (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            number TEXT NOT NULL,
            period TEXT NULL,
            type INTEGER NOT NULL,
            date TEXT NOT NULL,
            total REAL NOT NULL,
            seller_tax_id TEXT NULL,
            random_code TEXT NULL,
            items_json TEXT NULL,
            created_at TEXT NOT NULL,
            UNIQUE(number, period)
        );
        CREATE TABLE IF NOT EXISTS fixed_expenses (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            amount REAL NOT NULL,
            category TEXT NOT NULL,
            day_of_month INTEGER NOT NULL,
            start_date TEXT NOT NULL,
            end_date TEXT NULL,
            active INTEGER NOT NULL DEFAULT 1
        );
        CREATE TABLE IF NOT EXISTS transactions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            date TEXT NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('expense','income')),
            amount REAL NOT NULL,
            category TEXT NOT NULL,
            note TEXT NULL,
            source TEXT NOT NULL CHECK (source IN ('invoice','manual','fixed','lottery')),
            invoice_id INTEGER NULL REFERENCES invoices(id) ON DELETE CASCADE,
            fixed_expense_id INTEGER NULL REFERENCES fixed_expenses(id) ON DELETE SET NULL,
            fixed_month TEXT NULL,
            created_at TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_fixed_month ON transactions(fixed_expense_id, fixed_month) WHERE source = 'fixed';
        CREATE UNIQUE INDEX IF NOT EXISTS ux_lottery_invoice ON transactions(invoice_id) WHERE source = 'lottery';
        CREATE INDEX IF NOT EXISTS ix_tx_date ON transactions(date);
        CREATE TABLE IF NOT EXISTS winning_numbers (
            period TEXT PRIMARY KEY,
            special TEXT NOT NULL,
            grand TEXT NOT NULL,
            first TEXT NOT NULL,
            extra_sixth TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS store_rules (seller_tax_id TEXT PRIMARY KEY, category TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS item_rules (item_name TEXT PRIMARY KEY, category TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS business_registry (tax_id TEXT PRIMARY KEY, name TEXT NULL, industry TEXT NULL);
        """;
}
