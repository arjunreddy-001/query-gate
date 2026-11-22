using System.Text.RegularExpressions;
using DbAccessManager.Data;
using DbAccessManager.Services.Providers;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Services;

public sealed record PiiFinding(
    Guid DatabaseId,
    string DatabaseName,
    string Schema,
    string TableName,
    string ColumnName,
    string DataType,
    string DetectedCategory,
    string ComplianceStandard,
    string RecommendedStrategy,
    bool IsAlreadyProtected,
    string? MatchedRuleName = null);

public sealed class PiiDiscoveryService(
    MetadataService metadataService,
    IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private static readonly (string Category, string Standard, string Strategy, string[] Keywords)[] PiiCategories =
    [
        ("Financial & Cardholder Data", "PCI-DSS, GDPR", "CreditCard", ["card", "pan", "credit", "account_no", "accountno", "iban", "cvv", "card_number", "cvc", "exp_date"]),
        ("National & Tax Identifiers", "GDPR, SOC 2", "Ssn", ["ssn", "social_security", "tax_id", "national_id", "sin", "nric", "aadhar", "passport"]),
        ("Electronic Contact & Identity", "GDPR, CCPA", "Email", ["email", "e_mail", "mail_addr", "user_email", "contact_email"]),
        ("Phone & Mobile Numbers", "GDPR, CCPA", "Phone", ["phone", "mobile", "cell", "telephone", "fax", "tel_no", "phonenumber"]),
        ("Individual & Patient Names", "HIPAA, GDPR", "Name", ["patient_name", "first_name", "last_name", "firstname", "lastname", "full_name", "fullname", "surname"]),
        ("Medical & Health Identifiers", "HIPAA", "MedicalRecord", ["mrn", "patient_id", "medical_record", "diagnosis", "health_id", "prescription", "med_record"]),
        ("Birth & Demographic Dates", "HIPAA, GDPR", "DateOfBirth", ["dob", "birth_date", "birthdate", "date_of_birth", "birthday"]),
        ("Physical Addresses & Locations", "GDPR, CCPA", "Address", ["street", "address", "addr_line", "billing_address", "shipping_address"]),
        ("Postal & ZIP Codes", "HIPAA, GDPR", "ZipCode", ["zip", "zipcode", "postal_code", "postcode", "zip_code"]),
        ("Compensation & Financial Records", "SOC 2, GDPR", "Salary", ["salary", "wage", "compensation", "bonus", "payroll", "hourly_rate", "earnings"]),
        ("Authentication & Security Secrets", "SOC 2, PCI-DSS", "Secret", ["password", "passwd", "secret", "token", "api_key", "apikey", "private_key", "auth_token", "hash"])
    ];

    public async Task<List<PiiFinding>> ScanDatabaseAsync(Guid databaseId, CancellationToken ct = default)
    {
        var findings = new List<PiiFinding>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var managedDb = await db.ManagedDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == databaseId, ct);
        if (managedDb == null) return findings;

        var dbName = managedDb.Name;
        List<DbObjectInfo> objects;
        try
        {
            objects = await metadataService.GetAsync(databaseId);
        }
        catch
        {
            return findings;
        }

        var (activeRules, maskingEnabled) = await DataMaskingService.GetConfigAsync(dbFactory);

        foreach (var obj in objects.Where(o => o.Type == "Table" || o.Type == "View"))
        {
            foreach (var col in obj.Columns)
            {
                var cleanCol = col.Trim();
                var finding = DetectPii(databaseId, dbName, obj.Schema, obj.Name, cleanCol, "Column", activeRules, maskingEnabled);
                if (finding != null)
                {
                    findings.Add(finding);
                }
            }
        }

        return findings.OrderBy(f => f.IsAlreadyProtected).ThenBy(f => f.TableName).ThenBy(f => f.ColumnName).ToList();
    }

    private static PiiFinding? DetectPii(
        Guid dbId,
        string dbName,
        string schema,
        string table,
        string column,
        string dataType,
        IReadOnlyList<MaskingRule> activeRules,
        bool maskingEnabled)
    {
        var lowerCol = column.ToLowerInvariant();
        var tokens = lowerCol.Split(['_', ' ', '-'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var (category, standard, strategy, keywords) in PiiCategories)
        {
            var isMatch = keywords.Any(kw =>
                lowerCol == kw ||
                lowerCol.Contains(kw) ||
                tokens.Contains(kw));

            if (isMatch)
            {
                var matchedRule = maskingEnabled
                    ? DataMaskingService.MatchRule(column, activeRules)
                    : null;
                var isProtected = matchedRule != null;

                return new PiiFinding(
                    dbId,
                    dbName,
                    schema,
                    table,
                    column,
                    dataType,
                    category,
                    standard,
                    strategy,
                    isProtected,
                    matchedRule?.Name);
            }
        }

        return null;
    }
}
