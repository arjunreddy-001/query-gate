using DbAccessManager.Data;
using DbAccessManager.Security;
using Xunit;

namespace DbAccessManager.Tests;

public class MultiDatabaseSqlGateTests
{
    private readonly SqlGate _gate = new();

    [Fact]
    public void Postgres_select_with_limit_and_quoted_identifier()
    {
        var a = _gate.Analyze("SELECT * FROM \"public\".\"users\" WHERE \"id\" = 1 LIMIT 10;", DatabaseEngine.PostgreSql);
        Assert.False(a.HasHardDenial);
        Assert.Equal([Perm.Select], a.Requirements.ToArray());
        Assert.True(a.IsReadOnly);
    }

    [Fact]
    public void Postgres_dollar_quoted_string_does_not_break_statements()
    {
        var a = _gate.Analyze("SELECT $$first; statement; with semicolon$$ AS val;", DatabaseEngine.PostgreSql);
        Assert.False(a.HasHardDenial);
        Assert.Single(a.Batches);
        Assert.Contains(Perm.Select, a.Requirements);
    }

    [Fact]
    public void Postgres_semicolon_splits_multiple_statements()
    {
        var a = _gate.Analyze("SELECT 1; INSERT INTO users(name) VALUES ('Alice');", DatabaseEngine.PostgreSql);
        Assert.False(a.HasHardDenial);
        Assert.Equal(2, a.Batches.Count);
        Assert.Contains(Perm.Select, a.Requirements);
        Assert.Contains(Perm.Insert, a.Requirements);
    }

    [Theory]
    [InlineData("INSERT INTO users(a) VALUES (1);", Perm.Insert)]
    [InlineData("UPDATE users SET a = 1 WHERE b = 2;", Perm.Update)]
    [InlineData("DELETE FROM users WHERE b = 2;", Perm.Delete)]
    [InlineData("TRUNCATE TABLE users;", Perm.Truncate)]
    [InlineData("DROP TABLE users;", Perm.Drop)]
    [InlineData("ALTER TABLE users ADD COLUMN c int;", Perm.Alter)]
    [InlineData("CREATE TABLE users2(a int);", Perm.Create)]
    [InlineData("CALL do_something();", Perm.Execute)]
    public void Postgres_statements_map_to_permissions(string sql, string expected)
    {
        var a = _gate.Analyze(sql, DatabaseEngine.PostgreSql);
        Assert.False(a.HasHardDenial);
        Assert.Contains(expected, a.Requirements);
    }

    [Fact]
    public void Postgres_unfiltered_writes_flagged()
    {
        Assert.NotEmpty(_gate.Analyze("UPDATE users SET a = 1;", DatabaseEngine.PostgreSql).UnfilteredWrites);
        Assert.NotEmpty(_gate.Analyze("DELETE FROM users;", DatabaseEngine.PostgreSql).UnfilteredWrites);
        Assert.Empty(_gate.Analyze("DELETE FROM users WHERE a = 1;", DatabaseEngine.PostgreSql).UnfilteredWrites);
    }

    [Theory]
    [InlineData("GRANT SELECT ON users TO public;")]
    [InlineData("REVOKE ALL ON users FROM public;")]
    [InlineData("CREATE USER evil WITH PASSWORD 'pass';")]
    [InlineData("DROP USER evil;")]
    [InlineData("CREATE ROLE superadmin;")]
    [InlineData("DROP ROLE superadmin;")]
    public void Postgres_hard_denials(string sql)
    {
        var a = _gate.Analyze(sql, DatabaseEngine.PostgreSql);
        Assert.True(a.HasHardDenial);
    }

    [Fact]
    public void MySql_select_with_backticks_and_limit()
    {
        var a = _gate.Analyze("SELECT `id`, `name` FROM `users` WHERE `status` = 'active' LIMIT 50;", DatabaseEngine.MySql);
        Assert.False(a.HasHardDenial);
        Assert.Equal([Perm.Select], a.Requirements.ToArray());
        Assert.True(a.IsReadOnly);
    }

    [Theory]
    [InlineData("SHOW TABLES;")]
    [InlineData("SHOW COLUMNS FROM `users`;")]
    [InlineData("DESCRIBE `users`;")]
    [InlineData("DESC `users`;")]
    [InlineData("EXPLAIN SELECT * FROM `users`;")]
    public void MySql_show_and_describe_map_to_select(string sql)
    {
        var a = _gate.Analyze(sql, DatabaseEngine.MySql);
        Assert.False(a.HasHardDenial);
        Assert.Contains(Perm.Select, a.Requirements);
    }

    [Fact]
    public void MySql_hash_comments_are_respected()
    {
        var sql = "# This is a MySQL hash comment\nSELECT * FROM `orders` WHERE `id` = 1;";
        var a = _gate.Analyze(sql, DatabaseEngine.MySql);
        Assert.False(a.HasHardDenial);
        Assert.Equal([Perm.Select], a.Requirements.ToArray());
    }

    [Fact]
    public void MySql_unfiltered_writes_flagged()
    {
        Assert.NotEmpty(_gate.Analyze("UPDATE `products` SET `price` = 10;", DatabaseEngine.MySql).UnfilteredWrites);
        Assert.NotEmpty(_gate.Analyze("DELETE FROM `products`;", DatabaseEngine.MySql).UnfilteredWrites);
        Assert.Empty(_gate.Analyze("UPDATE `products` SET `price` = 10 WHERE `id` = 1;", DatabaseEngine.MySql).UnfilteredWrites);
    }

    [Theory]
    [InlineData("USE other_db;")]
    [InlineData("GRANT ALL ON *.* TO 'root'@'%';")]
    [InlineData("CREATE USER 'bad_actor'@'%' IDENTIFIED BY 'pass';")]
    [InlineData("DROP USER 'bad_actor'@'%';")]
    public void MySql_hard_denials(string sql)
    {
        var a = _gate.Analyze(sql, DatabaseEngine.MySql);
        Assert.True(a.HasHardDenial);
    }

    [Fact]
    public void MariaDb_shares_mysql_dialect_rules()
    {
        var a = _gate.Analyze("SELECT `id` FROM `customers` LIMIT 10;", DatabaseEngine.MariaDb);
        Assert.False(a.HasHardDenial);
        Assert.Contains(Perm.Select, a.Requirements);

        var denial = _gate.Analyze("USE information_schema;", DatabaseEngine.MariaDb);
        Assert.True(denial.HasHardDenial);
    }
}
