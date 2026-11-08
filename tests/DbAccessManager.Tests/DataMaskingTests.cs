using DbAccessManager.Services;
using Xunit;

namespace DbAccessManager.Tests;

public class DataMaskingTests
{
    [Theory]
    [InlineData("Email", DataMaskingService.MaskingStrategy.Email)]
    [InlineData("user_email_address", DataMaskingService.MaskingStrategy.Email)]
    [InlineData("CreditCardNumber", DataMaskingService.MaskingStrategy.CreditCard)]
    [InlineData("card_pan", DataMaskingService.MaskingStrategy.CreditCard)]
    [InlineData("IbanCode", DataMaskingService.MaskingStrategy.CreditCard)]
    [InlineData("SSN", DataMaskingService.MaskingStrategy.Ssn)]
    [InlineData("social_security_num", DataMaskingService.MaskingStrategy.Ssn)]
    [InlineData("TaxId", DataMaskingService.MaskingStrategy.Ssn)]
    [InlineData("PhoneNumber", DataMaskingService.MaskingStrategy.Phone)]
    [InlineData("mobile_no", DataMaskingService.MaskingStrategy.Phone)]
    [InlineData("AnnualSalary", DataMaskingService.MaskingStrategy.Salary)]
    [InlineData("hourly_rate", DataMaskingService.MaskingStrategy.Salary)]
    [InlineData("PasswordHash", DataMaskingService.MaskingStrategy.Secret)]
    [InlineData("ApiKey", DataMaskingService.MaskingStrategy.Secret)]
    [InlineData("token", DataMaskingService.MaskingStrategy.Secret)]
    [InlineData("patient_name", DataMaskingService.MaskingStrategy.Name)]
    [InlineData("first_name", DataMaskingService.MaskingStrategy.Name)]
    [InlineData("last_name", DataMaskingService.MaskingStrategy.Name)]
    [InlineData("mrn", DataMaskingService.MaskingStrategy.MedicalRecord)]
    [InlineData("patient_id", DataMaskingService.MaskingStrategy.MedicalRecord)]
    [InlineData("dob", DataMaskingService.MaskingStrategy.DateOfBirth)]
    [InlineData("date_of_birth", DataMaskingService.MaskingStrategy.DateOfBirth)]
    [InlineData("street_address", DataMaskingService.MaskingStrategy.Address)]
    [InlineData("zip_code", DataMaskingService.MaskingStrategy.ZipCode)]
    [InlineData("ip_address", DataMaskingService.MaskingStrategy.IpAddress)]
    [InlineData("ProductName", DataMaskingService.MaskingStrategy.None)]
    [InlineData("CreatedDate", DataMaskingService.MaskingStrategy.None)]
    public void DetectRule_Identifies_Sensitive_Columns(string columnName, DataMaskingService.MaskingStrategy expected)
    {
        var result = DataMaskingService.DetectRule(columnName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MaskName_Preserves_Word_Boundaries_Without_Revealing_Complete_Name()
    {
        var maskedFullName = DataMaskingService.MaskName("Johnathan Smith");
        Assert.Equal("J***n S***h", maskedFullName);

        var maskedSingle = DataMaskingService.MaskName("Alice");
        Assert.Equal("A***e", maskedSingle);

        var maskedShort = DataMaskingService.MaskName("Jo Li");
        Assert.Equal("J* L*", maskedShort);

        var maskedInitial = DataMaskingService.MaskName("J Smith");
        Assert.Equal("J S***h", maskedInitial);
    }

    [Fact]
    public void MaskDateOfBirth_Preserves_Only_Year_Under_Hipaa()
    {
        var maskedFromDateTime = DataMaskingService.MaskDateOfBirth(new DateTime(1988, 7, 23));
        Assert.Equal("1988-••-••", maskedFromDateTime);

        var maskedFromString = DataMaskingService.MaskDateOfBirth("1974-11-05 00:00:00");
        Assert.Equal("1974-••-••", maskedFromString);

        var maskedUnknown = DataMaskingService.MaskDateOfBirth("unknown-date");
        Assert.Equal("••••-••-••", maskedUnknown);
    }

    [Fact]
    public void MaskAddress_Redacts_Street_Address()
    {
        var masked = DataMaskingService.MaskAddress("742 Evergreen Terrace, Springfield, OR");
        Assert.Equal("••• [REDACTED STREET], Springfield, OR", masked);

        var maskedSingle = DataMaskingService.MaskAddress("123 Maple Street");
        Assert.Equal("••• [REDACTED STREET]", maskedSingle);
    }

    [Fact]
    public void MaskZipCode_Preserves_First_3_Digits_Under_Hipaa()
    {
        var masked = DataMaskingService.MaskZipCode("90210");
        Assert.Equal("902••", masked);

        var maskedZipPlus4 = DataMaskingService.MaskZipCode("90210-4321");
        Assert.Equal("902••", maskedZipPlus4);
    }

    [Fact]
    public void MaskMedicalRecord_Redacts_Preserving_Prefix_And_Trailing_Digits()
    {
        var maskedMrn = DataMaskingService.MaskMedicalRecord("MRN-984210");
        Assert.Equal("MRN-••••4210", maskedMrn);

        var maskedId = DataMaskingService.MaskMedicalRecord("12345678");
        Assert.Equal("••••••••5678", maskedId);
    }

    [Fact]
    public void MaskIpAddress_Redacts_Host_Octets()
    {
        var maskedIpv4 = DataMaskingService.MaskIpAddress("192.168.1.55");
        Assert.Equal("192.168.•••.•••", maskedIpv4);
    }

    [Fact]
    public void MaskEmail_Preserves_First_Last_And_Domain()
    {
        var masked = DataMaskingService.MaskEmail("alexander@contoso.com");
        Assert.Equal("a***r@contoso.com", masked);

        var shortMasked = DataMaskingService.MaskEmail("ab@contoso.com");
        Assert.Equal("a*@contoso.com", shortMasked);
    }

    [Fact]
    public void MaskCreditCard_Preserves_Last_4_Digits()
    {
        var masked = DataMaskingService.MaskCreditCard("4532-1234-5678-9012");
        Assert.Equal("•••• •••• •••• 9012", masked);
    }

    [Fact]
    public void MaskSsn_Preserves_Last_4_Digits()
    {
        var masked = DataMaskingService.MaskSsn("123-45-6789");
        Assert.Equal("•••-••-6789", masked);
    }

    [Fact]
    public void MaskPhone_Preserves_Last_4_Digits()
    {
        var masked = DataMaskingService.MaskPhone("+1 (555) 234-5678");
        Assert.Equal("•••-•••-5678", masked);
    }

    [Fact]
    public void MaskSalary_Zeroes_Numeric_Values()
    {
        var masked = DataMaskingService.MaskSalary(125000.50m);
        Assert.Equal(0.00m, masked);
    }

    [Fact]
    public void MaskSecret_Redacts_To_Redacted()
    {
        var masked = DataMaskingService.MaskValue("super_secret_api_token_xyz", DataMaskingService.MaskingStrategy.Secret);
        Assert.Equal("[REDACTED]", masked);
    }

    [Fact]
    public void ApplyMasking_Mutates_ResultSets_When_Sensitive_Columns_Present()
    {
        var execResult = new ExecutionResult();
        var rs = new ResultSetDto
        {
            Columns = ["UserId", "Email", "Salary", "Notes"],
            Rows =
            [
                [101, "clark.kent@dailyplanet.com", 85000m, "Reporter"],
                [102, "bruce.wayne@waynecorp.com", 1000000m, "Executive"]
            ]
        };
        execResult.ResultSets.Add(rs);

        DataMaskingService.ApplyMasking(execResult);

        Assert.True(execResult.IsMasked);
        Assert.Contains("Email", execResult.MaskedColumns);
        Assert.Contains("Salary", execResult.MaskedColumns);
        Assert.True(rs.IsMasked);
        Assert.Contains("Email", rs.MaskedColumns);
        Assert.Contains("Salary", rs.MaskedColumns);

        // Verify values were masked
        Assert.Equal("c***t@dailyplanet.com", rs.Rows[0][1]);
        Assert.Equal(0.00m, rs.Rows[0][2]);
        Assert.Equal("Reporter", rs.Rows[0][3]); // Non-sensitive preserved

        Assert.Equal("b***e@waynecorp.com", rs.Rows[1][1]);
        Assert.Equal(0.00m, rs.Rows[1][2]);
    }

    [Fact]
    public void ApplyMasking_Leaves_NonSensitive_ResultSets_Untouched()
    {
        var execResult = new ExecutionResult();
        var rs = new ResultSetDto
        {
            Columns = ["ProductId", "ProductName", "UnitPrice"],
            Rows =
            [
                [1, "Widget", 19.99m]
            ]
        };
        execResult.ResultSets.Add(rs);

        DataMaskingService.ApplyMasking(execResult);

        Assert.False(execResult.IsMasked);
        Assert.Empty(execResult.MaskedColumns);
        Assert.False(rs.IsMasked);
        Assert.Equal("Widget", rs.Rows[0][1]);
    }

    [Fact]
    public void MatchRule_Matches_Custom_Keywords_And_Case_Insensitive()
    {
        var customRule = new MaskingRule
        {
            Name = "Healthcare Identifiers",
            Patterns = "patient_id, mrn, diagnosis_code",
            Strategy = DataMaskingService.MaskingStrategy.CustomText,
            CustomMask = "[PROTECTED HEALTH INFO]"
        };

        var matched = DataMaskingService.MatchRule("Patient_Id_Number", [customRule]);
        Assert.NotNull(matched);
        Assert.Equal("Healthcare Identifiers", matched.Name);

        var noMatch = DataMaskingService.MatchRule("OrderTotal", [customRule]);
        Assert.Null(noMatch);
    }

    [Fact]
    public void CustomText_Strategy_Uses_Specified_Replacement()
    {
        var val = DataMaskingService.MaskValue("TopSecretBlueprint", DataMaskingService.MaskingStrategy.CustomText, "[CLASSIFIED]");
        Assert.Equal("[CLASSIFIED]", val);

        var valDefault = DataMaskingService.MaskValue("TopSecretBlueprint", DataMaskingService.MaskingStrategy.CustomText, null);
        Assert.Equal("[CONFIDENTIAL]", valDefault);
    }

    [Fact]
    public void Disabled_Rule_Is_Ignored_During_Matching()
    {
        var rule = new MaskingRule
        {
            Name = "Inactive Email Rule",
            Patterns = "email",
            Strategy = DataMaskingService.MaskingStrategy.Email,
            IsEnabled = false
        };

        var matched = DataMaskingService.MatchRule("customer_email", [rule]);
        Assert.Null(matched);
    }

    [Fact]
    public void TestMask_Provides_Accurate_Matching_And_Preview()
    {
        var rules = DataMaskingService.GetDefaultRules();

        var (matched, rule, masked) = DataMaskingService.TestMask("work_email_address", "bruce.wayne@wayne.com", rules, true);
        Assert.True(matched);
        Assert.NotNull(rule);
        Assert.Equal("Email Addresses", rule.Name);
        Assert.Equal("b***e@wayne.com", masked);

        var (notMatched, _, unmasked) = DataMaskingService.TestMask("ProductName", "Gadget", rules, true);
        Assert.False(notMatched);
        Assert.Equal("Gadget", unmasked);
    }

    [Fact]
    public void ApplyMasking_With_Custom_Rules_Applies_Configured_Strategies()
    {
        var customRules = new List<MaskingRule>
        {
            new()
            {
                Name = "License Keys",
                Patterns = "license_key, prod_key",
                Strategy = DataMaskingService.MaskingStrategy.CustomText,
                CustomMask = "XXXX-XXXX-XXXX"
            }
        };

        var execResult = new ExecutionResult();
        var rs = new ResultSetDto
        {
            Columns = ["Id", "license_key", "Description"],
            Rows =
            [
                [1, "ABCD-EFGH-1234", "Standard Enterprise License"]
            ]
        };
        execResult.ResultSets.Add(rs);

        DataMaskingService.ApplyMasking(execResult, customRules);

        Assert.True(execResult.IsMasked);
        Assert.Contains("license_key", execResult.MaskedColumns);
        Assert.Equal("XXXX-XXXX-XXXX", rs.Rows[0][1]);
        Assert.Equal("Standard Enterprise License", rs.Rows[0][2]);
    }

    [Fact]
    public void DefaultRules_Cover_All_Compliance_Standards()
    {
        var rules = DataMaskingService.GetDefaultRules();
        Assert.NotEmpty(rules);

        var allCoveredStandards = rules.SelectMany(r => r.GetStandardList())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("HIPAA", allCoveredStandards);
        Assert.Contains("GDPR", allCoveredStandards);
        Assert.Contains("PCI-DSS", allCoveredStandards);
        Assert.Contains("SOC 2", allCoveredStandards);
        Assert.Contains("CCPA", allCoveredStandards);

        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
            Assert.False(string.IsNullOrWhiteSpace(rule.Patterns));
            Assert.False(string.IsNullOrWhiteSpace(rule.Standards));
            Assert.NotEmpty(rule.GetPatternList());
            Assert.NotEmpty(rule.GetStandardList());
        }
    }

    [Fact]
    public void MaskingRule_GetStandardList_Parses_Standards_Correctly()
    {
        var rule = new MaskingRule
        {
            Name = "Multi-Regulation",
            Standards = "GDPR, HIPAA, PCI-DSS, SOC 2, ISO 27001"
        };

        var list = rule.GetStandardList().ToList();
        Assert.Equal(5, list.Count);
        Assert.Contains("GDPR", list);
        Assert.Contains("HIPAA", list);
        Assert.Contains("PCI-DSS", list);
        Assert.Contains("SOC 2", list);
        Assert.Contains("ISO 27001", list);
    }

    [Fact]
    public void StandardBadgeClass_Returns_Expected_CSS_Classes()
    {
        Assert.Equal("info", DataMaskingService.StandardBadgeClass("HIPAA"));
        Assert.Equal("brand", DataMaskingService.StandardBadgeClass("GDPR"));
        Assert.Equal("ok", DataMaskingService.StandardBadgeClass("PCI-DSS"));
        Assert.Equal("warn", DataMaskingService.StandardBadgeClass("SOC 2"));
        Assert.Equal("", DataMaskingService.StandardBadgeClass("OTHER"));
    }

    [Fact]
    public void SaveConfig_Generates_Individual_Keys_Under_2000_Chars()
    {
        var defaultRules = DataMaskingService.GetDefaultRules();
        foreach (var rule in defaultRules)
        {
            var key = DataMaskingService.RulePrefix + rule.Id;
            var json = System.Text.Json.JsonSerializer.Serialize(rule);

            Assert.StartsWith("DataMasking:Rule:", key);
            Assert.True(key.Length <= 100, $"Key length {key.Length} exceeds 100 char limit");
            Assert.True(json.Length < 2000, $"Rule '{rule.Name}' JSON length {json.Length} exceeds 2,000 char column limit");
        }
    }

    [Fact]
    public void DefaultRules_Are_All_Marked_As_BuiltIn()
    {
        var defaultRules = DataMaskingService.GetDefaultRules();
        Assert.NotEmpty(defaultRules);
        Assert.All(defaultRules, r => Assert.True(r.IsBuiltIn));
    }

    [Fact]
    public void Filter_By_RuleType_And_Standards_Correctly_Partitions_Rules()
    {
        var rules = DataMaskingService.GetDefaultRules();
        var customRule = new MaskingRule
        {
            Name = "Custom Internal Token",
            Patterns = "internal_auth_token",
            Strategy = DataMaskingService.MaskingStrategy.Secret,
            Standards = "SOC 2, ISO 27001",
            IsBuiltIn = false
        };
        rules.Add(customRule);

        // Test Built-in vs Custom partition
        var builtInOnly = rules.Where(r => r.IsBuiltIn).ToList();
        var customOnly = rules.Where(r => !r.IsBuiltIn).ToList();

        Assert.Equal(14, builtInOnly.Count);
        Assert.Single(customOnly);
        Assert.Equal("Custom Internal Token", customOnly[0].Name);

        // Test HIPAA standard filter
        var hipaaRules = rules.Where(r => r.GetStandardList().Contains("HIPAA", StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(hipaaRules);
        Assert.DoesNotContain(customRule, hipaaRules);

        // Test SOC 2 standard filter includes customRule
        var soc2Rules = rules.Where(r => r.GetStandardList().Contains("SOC 2", StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.Contains(customRule, soc2Rules);
    }
}
