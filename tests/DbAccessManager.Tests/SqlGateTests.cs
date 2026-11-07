using DbAccessManager.Security;
using Xunit;

namespace DbAccessManager.Tests;

public class SqlGateTests
{
    private readonly SqlGate _gate = new();

    [Fact]
    public void Select_requires_only_select()
    {
        var a = _gate.Analyze("SELECT * FROM dbo.Users WHERE Id = 1");
        Assert.False(a.HasHardDenial);
        Assert.Equal([Perm.Select], a.Requirements.ToArray());
        Assert.True(a.IsReadOnly);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.T(a) VALUES (1)", Perm.Insert)]
    [InlineData("UPDATE dbo.T SET a = 1 WHERE b = 2", Perm.Update)]
    [InlineData("DELETE FROM dbo.T WHERE b = 2", Perm.Delete)]
    [InlineData("TRUNCATE TABLE dbo.T", Perm.Truncate)]
    [InlineData("DROP TABLE dbo.T", Perm.Drop)]
    [InlineData("ALTER TABLE dbo.T ADD c int", Perm.Alter)]
    [InlineData("CREATE TABLE dbo.T2(a int)", Perm.Create)]
    [InlineData("EXEC dbo.DoThing", Perm.Execute)]
    public void Statements_map_to_permissions(string sql, string expected)
    {
        var a = _gate.Analyze(sql);
        Assert.False(a.HasHardDenial, string.Join(";", a.HardDenials.Concat(a.ParseErrors)));
        Assert.Contains(expected, a.Requirements);
    }

    [Fact]
    public void Comments_cannot_hide_statements()
    {
        var a = _gate.Analyze("SELECT 1 /* harmless */; -- DROP TABLE x\nDELETE FROM dbo.T WHERE 1=1; /* DROP */ DROP TABLE dbo.Y");
        Assert.Contains(Perm.Delete, a.Requirements);
        Assert.Contains(Perm.Drop, a.Requirements);
    }

    [Fact]
    public void Strings_are_not_parsed_as_statements()
    {
        var a = _gate.Analyze("SELECT 'DROP TABLE x; GO' AS s");
        Assert.Equal([Perm.Select], a.Requirements.ToArray());
    }

    [Fact]
    public void Go_separates_batches_case_insensitively()
    {
        var a = _gate.Analyze("SELECT 1\ngo\nDROP TABLE dbo.T\nGO");
        Assert.Equal(2, a.Batches.Count);
        Assert.Contains(Perm.Drop, a.Requirements);
    }

    [Fact]
    public void Dynamic_sql_requires_dynamic_permission()
    {
        Assert.Contains(Perm.DynamicSql, _gate.Analyze("EXEC('SELECT 1')").Requirements);
        Assert.Contains(Perm.DynamicSql, _gate.Analyze("EXEC sp_executesql N'SELECT 1'").Requirements);
    }

    [Fact]
    public void Xp_cmdshell_requires_its_permission()
    {
        Assert.Contains(Perm.XpCmdShell, _gate.Analyze("EXEC xp_cmdshell 'dir'").Requirements);
    }

    [Fact]
    public void Openrowset_requires_linked_server_permission()
    {
        var a = _gate.Analyze("SELECT * FROM OPENROWSET('SQLNCLI','Server=x;','SELECT 1')");
        Assert.True(a.HasHardDenial || a.Requirements.Contains(Perm.LinkedServer));
    }

    [Theory]
    [InlineData("USE master")]
    [InlineData("SELECT * FROM OtherDb.dbo.T")]
    [InlineData("GRANT SELECT ON dbo.T TO public")]
    [InlineData("CREATE LOGIN x WITH PASSWORD = 'a'")]
    [InlineData("SELECT * FROM")]
    [InlineData("")]
    public void Hard_denials(string sql)
    {
        Assert.True(_gate.Analyze(sql).HasHardDenial);
    }

    [Fact]
    public void Unfiltered_writes_are_flagged()
    {
        Assert.NotEmpty(_gate.Analyze("UPDATE dbo.T SET a = 1").UnfilteredWrites);
        Assert.NotEmpty(_gate.Analyze("DELETE FROM dbo.T").UnfilteredWrites);
        Assert.Empty(_gate.Analyze("DELETE FROM dbo.T WHERE a = 1").UnfilteredWrites);
    }

    [Fact]
    public void Sysproc_requires_specific_permission()
    {
        var a = _gate.Analyze("EXEC sp_who2");
        Assert.Contains(a.Requirements, r => r.StartsWith(Perm.SysProcPrefix, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Helptext_is_its_own_permission()
    {
        Assert.Contains(Perm.SpHelpText, _gate.Analyze("EXEC sp_helptext 'dbo.p'").Requirements);
    }
}
