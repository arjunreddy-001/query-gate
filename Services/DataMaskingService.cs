using System.Text.Json;
using System.Text.RegularExpressions;
using DbAccessManager.Data;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Services;

public sealed class MaskingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Patterns { get; set; } = ""; // Comma-separated column keywords
    public DataMaskingService.MaskingStrategy Strategy { get; set; } = DataMaskingService.MaskingStrategy.Secret;
    public string? CustomMask { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    public string? Description { get; set; }
    public string Standards { get; set; } = ""; // E.g. "GDPR, HIPAA, PCI-DSS, SOC 2"
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public IEnumerable<string> GetPatternList() =>
        Patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IEnumerable<string> GetStandardList() =>
        Standards.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class DataMaskingService
{
    public const string RulePrefix = "DataMasking:Rule:";
    public const string SettingsKeyEnabled = "DataMasking:Enabled";

    public static readonly string[] KnownStandards = ["GDPR", "HIPAA", "PCI-DSS", "SOC 2", "CCPA", "FERPA"];

    private sealed class MaskingConfigCache
    {
        public required List<MaskingRule> Rules { get; init; }
        public required bool Enabled { get; init; }
    }

    private static volatile MaskingConfigCache? _cachedConfig;

    public enum MaskingStrategy
    {
        None,
        Name,
        MedicalRecord,
        DateOfBirth,
        Address,
        ZipCode,
        Email,
        CreditCard,
        Ssn,
        Phone,
        IpAddress,
        Salary,
        Secret,
        CustomText
    }

    public static void InvalidateCache() => _cachedConfig = null;

    public static string StandardBadgeClass(string standard) => standard.ToUpperInvariant() switch
    {
        "HIPAA" => "info",
        "GDPR" => "brand",
        "PCI-DSS" => "ok",
        "SOC 2" => "warn",
        _ => ""
    };

    public static List<MaskingRule> GetDefaultRules() =>
    [
        new MaskingRule
        {
            Name = "Credit Cards & Financial Accounts",
            Patterns = "card, pan, credit, account_no, accountno, accountnum, iban, credit_card, card_number",
            Strategy = MaskingStrategy.CreditCard,
            Standards = "PCI-DSS, GDPR, SOC 2, CCPA",
            IsBuiltIn = true,
            Description = "PCI-DSS Requirement 3.4 & GDPR: Redacts cardholder PANs and bank accounts, preserving last 4 digits"
        },
        new MaskingRule
        {
            Name = "Card CVV & Security Codes",
            Patterns = "cvv, cvc, cvv2, cvc2, card_code, pin, pin_code, security_code",
            Strategy = MaskingStrategy.Secret,
            Standards = "PCI-DSS, SOC 2",
            IsBuiltIn = true,
            Description = "PCI-DSS Requirement 3.2: Complete redaction of Sensitive Authentication Data (SAD) and PINs"
        },
        new MaskingRule
        {
            Name = "Patient & Person Names",
            Patterns = "patient_name, patientname, first_name, firstname, last_name, lastname, full_name, fullname, patient, physician_name, doctor_name",
            Strategy = MaskingStrategy.Name,
            Standards = "HIPAA, GDPR, CCPA",
            IsBuiltIn = true,
            Description = "HIPAA Safe Harbor & GDPR: Masks names while preserving initial and final characters (e.g. J***n S***h)"
        },
        new MaskingRule
        {
            Name = "Medical Record & Patient IDs",
            Patterns = "mrn, medical_record, patient_id, patientid, encounter_id, admission_id, chart_no, chart_number",
            Strategy = MaskingStrategy.MedicalRecord,
            Standards = "HIPAA, SOC 2",
            IsBuiltIn = true,
            Description = "HIPAA Safe Harbor: Redacts medical record, patient, and encounter identifiers (e.g. MRN-••••2014)"
        },
        new MaskingRule
        {
            Name = "Social Security & National Tax IDs",
            Patterns = "ssn, social_security, tax_id, taxid, national_id, aadhaar, sin_",
            Strategy = MaskingStrategy.Ssn,
            Standards = "GDPR, HIPAA, PCI-DSS, SOC 2, CCPA",
            IsBuiltIn = true,
            Description = "Government IDs & SSNs: Preserves last 4 digits across all data protection frameworks"
        },
        new MaskingRule
        {
            Name = "Dates of Birth & Clinical Dates",
            Patterns = "dob, birth_date, birthdate, date_of_birth, admission_date, discharge_date, death_date",
            Strategy = MaskingStrategy.DateOfBirth,
            Standards = "HIPAA, GDPR, CCPA",
            IsBuiltIn = true,
            Description = "HIPAA Safe Harbor & GDPR: Redacts month and day from dates, preserving only birth/encounter year (e.g. 1985-••-••)"
        },
        new MaskingRule
        {
            Name = "Physical Addresses & Locations",
            Patterns = "address, street_address, street, addr_line, residence",
            Strategy = MaskingStrategy.Address,
            Standards = "HIPAA, GDPR, CCPA",
            IsBuiltIn = true,
            Description = "HIPAA Safe Harbor & GDPR: Redacts street address and geographic identifiers smaller than a state"
        },
        new MaskingRule
        {
            Name = "Postal & ZIP Codes",
            Patterns = "zip, zip_code, zipcode, postal_code, postalcode",
            Strategy = MaskingStrategy.ZipCode,
            Standards = "HIPAA, GDPR, CCPA",
            IsBuiltIn = true,
            Description = "HIPAA Safe Harbor: Preserves initial 3 digits of 5-digit postal codes (e.g. 902••)"
        },
        new MaskingRule
        {
            Name = "Health Plan & Insurance Numbers",
            Patterns = "policy_no, policy_number, member_id, subscriber_id, insurance_id, beneficiary_id, medicaid, medicare",
            Strategy = MaskingStrategy.MedicalRecord,
            Standards = "HIPAA, SOC 2",
            IsBuiltIn = true,
            Description = "HIPAA Safe Harbor: Masks health plan and insurance beneficiary numbers (e.g. POL-••••789)"
        },
        new MaskingRule
        {
            Name = "Phone & Fax Numbers",
            Patterns = "phone, mobile, cell, telephone, fax",
            Strategy = MaskingStrategy.Phone,
            Standards = "HIPAA, GDPR, CCPA",
            IsBuiltIn = true,
            Description = "Preserves last 4 digits (e.g. •••-•••-1234)"
        },
        new MaskingRule
        {
            Name = "Email Addresses",
            Patterns = "email, mail_addr, mailaddress",
            Strategy = MaskingStrategy.Email,
            Standards = "GDPR, HIPAA, SOC 2, CCPA",
            IsBuiltIn = true,
            Description = "GDPR Article 4 & HIPAA: Preserves first/last characters and domain (e.g. j***e@domain.com)"
        },
        new MaskingRule
        {
            Name = "IP Addresses & Network IDs",
            Patterns = "ip_address, ipaddress, client_ip, remote_ip",
            Strategy = MaskingStrategy.IpAddress,
            Standards = "GDPR, HIPAA, SOC 2, CCPA",
            IsBuiltIn = true,
            Description = "GDPR Online Identifiers & HIPAA: Redacts host octets of IP addresses (e.g. 192.168.•••.•••)"
        },
        new MaskingRule
        {
            Name = "Passwords, Tokens & Credentials",
            Patterns = "password, secret, token, apikey, api_key, passwd, hash, pass_phrase, private_key",
            Strategy = MaskingStrategy.Secret,
            Standards = "SOC 2, PCI-DSS, GDPR, HIPAA",
            IsBuiltIn = true,
            Description = "SOC 2 CC6.1 & PCI-DSS: Completely redacts credentials, authentication tokens, and secrets"
        },
        new MaskingRule
        {
            Name = "Salary & Employee Compensation",
            Patterns = "salary, wage, compensation, bonus, hourly_rate",
            Strategy = MaskingStrategy.Salary,
            Standards = "SOC 2, GDPR",
            IsBuiltIn = true,
            Description = "SOC 2 Confidentiality & GDPR: Replaces numeric employee compensation with 0.00"
        }
    ];

    public static async Task<(List<MaskingRule> Rules, bool Enabled)> GetConfigAsync(IDbContextFactory<ApplicationDbContext> factory)
    {
        var cached = _cachedConfig;
        if (cached is not null) return (cached.Rules, cached.Enabled);

        await using var db = await factory.CreateDbContextAsync();
        var enabledSetting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingsKeyEnabled);
        var enabled = enabledSetting is null || !bool.TryParse(enabledSetting.Value, out var en) || en;

        // Fetch individual rule entries stored with prefix DataMasking:Rule:
        var ruleEntries = await db.Settings.AsNoTracking()
            .Where(s => s.Key.StartsWith(RulePrefix))
            .ToListAsync();

        var rules = new List<MaskingRule>();
        foreach (var entry in ruleEntries)
        {
            try
            {
                var rule = JsonSerializer.Deserialize<MaskingRule>(entry.Value);
                if (rule is not null) rules.Add(rule);
            }
            catch { }
        }

        // If no rules are stored in individual rows, check legacy single blob row
        if (rules.Count == 0)
        {
            var legacySetting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "DataMasking:Rules");
            if (legacySetting is not null && !string.IsNullOrWhiteSpace(legacySetting.Value))
            {
                try { rules = JsonSerializer.Deserialize<List<MaskingRule>>(legacySetting.Value) ?? []; }
                catch { }
            }
        }

        // If still empty, initialize with default rules
        if (rules.Count == 0)
        {
            rules = GetDefaultRules();
            // Automatically persist individual rows so they exist in database
            _ = Task.Run(async () =>
            {
                try { await SaveConfigAsync(factory, rules, enabled); }
                catch { }
            });
        }

        _cachedConfig = new MaskingConfigCache { Rules = rules, Enabled = enabled };
        return (rules, enabled);
    }

    public static async Task SaveConfigAsync(IDbContextFactory<ApplicationDbContext> factory, List<MaskingRule> rules, bool enabled)
    {
        await using var db = await factory.CreateDbContextAsync();

        // 1. Remove legacy single blob row if it exists
        var legacy = await db.Settings.FindAsync("DataMasking:Rules");
        if (legacy is not null) db.Settings.Remove(legacy);

        // 2. Fetch existing individual rule keys
        var existingRules = await db.Settings.Where(s => s.Key.StartsWith(RulePrefix)).ToListAsync();
        var currentKeys = new HashSet<string>(rules.Select(r => RulePrefix + r.Id));

        // Delete rules that are no longer in the list
        foreach (var old in existingRules)
        {
            if (!currentKeys.Contains(old.Key))
            {
                db.Settings.Remove(old);
            }
        }

        // Add or update rules as individual rows (each under 2,000 chars)
        foreach (var rule in rules)
        {
            var key = RulePrefix + rule.Id;
            var json = JsonSerializer.Serialize(rule);
            var entry = existingRules.FirstOrDefault(e => e.Key == key);
            if (entry is null)
            {
                db.Settings.Add(new SettingEntry { Key = key, Value = json });
            }
            else
            {
                entry.Value = json;
            }
        }

        // Save enabled state
        var existingEnabled = await db.Settings.FindAsync(SettingsKeyEnabled);
        if (existingEnabled is null)
        {
            db.Settings.Add(new SettingEntry { Key = SettingsKeyEnabled, Value = enabled ? "true" : "false" });
        }
        else
        {
            existingEnabled.Value = enabled ? "true" : "false";
        }

        await db.SaveChangesAsync();
        _cachedConfig = new MaskingConfigCache { Rules = rules, Enabled = enabled };
    }

    public static void ApplyMasking(ExecutionResult result, IReadOnlyList<MaskingRule>? rules = null)
    {
        if (result.ResultSets.Count == 0) return;
        var activeRules = (rules ?? GetDefaultRules()).Where(r => r.IsEnabled).ToList();
        if (activeRules.Count == 0) return;

        var anyMasked = false;
        var maskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rs in result.ResultSets)
        {
            var matchedRules = new MaskingRule?[rs.Columns.Count];
            var hasMaskedCol = false;

            for (var i = 0; i < rs.Columns.Count; i++)
            {
                var rule = MatchRule(rs.Columns[i], activeRules);
                matchedRules[i] = rule;
                if (rule is not null)
                {
                    hasMaskedCol = true;
                    anyMasked = true;
                    maskedColumns.Add(rs.Columns[i]);
                    if (!rs.MaskedColumns.Contains(rs.Columns[i], StringComparer.OrdinalIgnoreCase))
                    {
                        rs.MaskedColumns.Add(rs.Columns[i]);
                    }
                }
            }

            if (!hasMaskedCol) continue;

            rs.IsMasked = true;

            foreach (var row in rs.Rows)
            {
                for (var i = 0; i < rs.Columns.Count; i++)
                {
                    var rule = matchedRules[i];
                    if (rule is null || i >= row.Length) continue;
                    row[i] = MaskValue(row[i], rule.Strategy, rule.CustomMask);
                }
            }
        }

        if (anyMasked)
        {
            result.IsMasked = true;
            foreach (var col in maskedColumns)
            {
                if (!result.MaskedColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                {
                    result.MaskedColumns.Add(col);
                }
            }
        }
    }

    public static MaskingRule? MatchRule(string columnName, IEnumerable<MaskingRule> rules)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return null;
        var col = columnName.ToLowerInvariant();

        var activeRules = rules.Where(r => r.IsEnabled).ToList();

        // Sort candidate patterns by length descending so more specific patterns (e.g. "patient_name" or "patient_id")
        // take precedence over shorter general words (e.g. "patient").
        var candidate = (from rule in activeRules
                         from pattern in rule.GetPatternList()
                         where !string.IsNullOrWhiteSpace(pattern)
                         let p = pattern.ToLowerInvariant()
                         where col.Contains(p)
                         orderby p.Length descending
                         select rule).FirstOrDefault();

        return candidate;
    }

    public static (bool Matched, MaskingRule? Rule, object? MaskedValue) TestMask(
        string columnName, object? sampleValue, IEnumerable<MaskingRule> rules, bool enabled = true)
    {
        if (!enabled || string.IsNullOrWhiteSpace(columnName)) return (false, null, sampleValue);
        var rule = MatchRule(columnName, rules);
        if (rule is null) return (false, null, sampleValue);
        var masked = MaskValue(sampleValue, rule.Strategy, rule.CustomMask);
        return (true, rule, masked);
    }

    public static string GetStrategySample(MaskingStrategy strategy, string? customMask = null) => strategy switch
    {
        MaskingStrategy.Name => "J***n S***h",
        MaskingStrategy.MedicalRecord => "MRN-••••2014",
        MaskingStrategy.DateOfBirth => "1985-••-••",
        MaskingStrategy.Address => "••• [REDACTED STREET]",
        MaskingStrategy.ZipCode => "902••",
        MaskingStrategy.Email => "a***r@contoso.com",
        MaskingStrategy.CreditCard => "•••• •••• •••• 9012",
        MaskingStrategy.Ssn => "•••-••-6789",
        MaskingStrategy.Phone => "•••-•••-5678",
        MaskingStrategy.IpAddress => "192.168.•••.•••",
        MaskingStrategy.Salary => "0.00",
        MaskingStrategy.Secret => "[REDACTED]",
        MaskingStrategy.CustomText => string.IsNullOrWhiteSpace(customMask) ? "[CONFIDENTIAL]" : customMask,
        _ => "-"
    };

    public static MaskingStrategy DetectRule(string columnName)
    {
        var matched = MatchRule(columnName, GetDefaultRules());
        return matched?.Strategy ?? MaskingStrategy.None;
    }

    public static object? MaskValue(object? value, MaskingStrategy strategy, string? customMask = null)
    {
        if (value is null or DBNull) return value;
        var str = value.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(str)) return str;

        return strategy switch
        {
            MaskingStrategy.Name => MaskName(str),
            MaskingStrategy.MedicalRecord => MaskMedicalRecord(str),
            MaskingStrategy.DateOfBirth => MaskDateOfBirth(value),
            MaskingStrategy.Address => MaskAddress(str),
            MaskingStrategy.ZipCode => MaskZipCode(str),
            MaskingStrategy.Email => MaskEmail(str),
            MaskingStrategy.CreditCard => MaskCreditCard(str),
            MaskingStrategy.Ssn => MaskSsn(str),
            MaskingStrategy.Phone => MaskPhone(str),
            MaskingStrategy.IpAddress => MaskIpAddress(str),
            MaskingStrategy.Salary => MaskSalary(value),
            MaskingStrategy.Secret => "[REDACTED]",
            MaskingStrategy.CustomText => string.IsNullOrWhiteSpace(customMask) ? "[CONFIDENTIAL]" : customMask,
            _ => str
        };
    }

    public static string MaskName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return name;

        var maskedParts = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var clean = part.Trim();
            if (clean.Length <= 1)
            {
                maskedParts.Add(clean);
            }
            else if (clean.Length == 2)
            {
                maskedParts.Add($"{clean[0]}*");
            }
            else
            {
                maskedParts.Add($"{clean[0]}***{clean[^1]}");
            }
        }
        return string.Join(" ", maskedParts);
    }

    public static string MaskMedicalRecord(string mrn)
    {
        var clean = mrn.Trim();
        if (clean.Length <= 4) return new string('•', clean.Length);

        var dashIdx = clean.IndexOf('-');
        if (dashIdx > 0 && dashIdx < clean.Length - 3)
        {
            var prefix = clean[..(dashIdx + 1)];
            var remainder = clean[(dashIdx + 1)..];
            var last4 = remainder.Length > 4 ? remainder[^4..] : remainder[^2..];
            return $"{prefix}••••{last4}";
        }

        return $"••••••••{clean[^4..]}";
    }

    public static string MaskDateOfBirth(object value)
    {
        if (value is DateTime dt)
        {
            return $"{dt.Year}-••-••";
        }
        if (value is DateTimeOffset dto)
        {
            return $"{dto.Year}-••-••";
        }
        var str = value.ToString() ?? "";
        var match = Regex.Match(str, @"\b(19\d\d|20\d\d)\b");
        if (match.Success)
        {
            return $"{match.Value}-••-••";
        }
        return "••••-••-••";
    }

    public static string MaskAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return address;
        var parts = address.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1)
        {
            return "••• [REDACTED STREET], " + string.Join(", ", parts.Skip(1));
        }
        return "••• [REDACTED STREET]";
    }

    public static string MaskZipCode(string zip)
    {
        var digits = new string(zip.Where(char.IsDigit).ToArray());
        if (digits.Length >= 5)
        {
            return $"{digits[..3]}••";
        }
        if (digits.Length >= 3)
        {
            return $"{digits[..3]}••";
        }
        return "•••••";
    }

    public static string MaskIpAddress(string ip)
    {
        var clean = ip.Trim();
        if (clean.Contains('.'))
        {
            var octets = clean.Split('.');
            if (octets.Length == 4)
            {
                return $"{octets[0]}.{octets[1]}.•••.•••";
            }
        }
        if (clean.Contains(':'))
        {
            var parts = clean.Split(':');
            if (parts.Length > 2)
            {
                return $"{parts[0]}:{parts[1]}:••••:••••";
            }
        }
        return "•••.•••.•••.•••";
    }

    public static string MaskEmail(string email)
    {
        var parts = email.Split('@');
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0])) return "••••@••••.com";
        var user = parts[0];
        var domain = parts[1];
        var maskedUser = user.Length switch
        {
            <= 2 => $"{user[0]}*",
            _ => $"{user[0]}***{user[^1]}"
        };
        return $"{maskedUser}@{domain}";
    }

    public static string MaskCreditCard(string card)
    {
        var digits = new string(card.Where(char.IsDigit).ToArray());
        if (digits.Length >= 4)
        {
            var last4 = digits[^4..];
            return $"•••• •••• •••• {last4}";
        }
        return "•••• •••• •••• ••••";
    }

    public static string MaskSsn(string ssn)
    {
        var digits = new string(ssn.Where(char.IsDigit).ToArray());
        if (digits.Length >= 4)
        {
            return $"•••-••-{digits[^4..]}";
        }
        return "•••-••-••••";
    }

    public static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length >= 4)
        {
            return $"•••-•••-{digits[^4..]}";
        }
        return "•••-•••-••••";
    }

    public static object MaskSalary(object value)
    {
        if (value is decimal or double or float or int or long or short or byte)
        {
            return 0.00m;
        }
        return "[REDACTED]";
    }
}
