using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoiCalendar.Tests;

public sealed partial class ProductionSecurityHardeningTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string MigrationDirectory =
        Path.Combine(RepositoryRoot, "supabase", "migrations");
    private static readonly string AllMigrations = string.Join(
        Environment.NewLine,
        Directory.GetFiles(MigrationDirectory, "*.sql")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(File.ReadAllText));
    private static readonly string HardeningMigration = File.ReadAllText(Path.Combine(
        MigrationDirectory,
        "20260911000000_production_security_hardening.sql"));
    private static readonly string EdgeFunction = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "supabase",
        "functions",
        "delete-account",
        "index.ts"));

    [Fact]
    public void EveryUserOwnedOrSensitiveTable_HasRlsEnabled()
    {
        foreach (var table in new[]
        {
            "profiles",
            "sync_state",
            "devices",
            "calendars",
            "calendar_events",
            "calendar_event_mutations",
            "account_deletion_rate_limits"
        })
        {
            Assert.Contains(
                $"alter table public.{table} enable row level security",
                AllMigrations,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BrowserRole_CannotBypassOwnerRpcsWithDirectTableAccess()
    {
        foreach (var table in new[]
        {
            "profiles",
            "devices",
            "calendars",
            "calendar_events",
            "calendar_event_mutations"
        })
        {
            Assert.Contains(
                $"revoke all on table public.{table} from anon, authenticated",
                HardeningMigration,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(
            "using ((select auth.uid()) = owner_id)",
            AllMigrations,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "with check ((select auth.uid()) = owner_id)",
            AllMigrations,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "create policy",
            HardeningMigration[
                HardeningMigration.IndexOf(
                    "account_deletion_rate_limits",
                    StringComparison.Ordinal)..],
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OwnerRpcs_RejectUnauthenticatedAndCrossOwnerOperations()
    {
        Assert.Contains(
            "v_owner_id uuid := auth.uid()",
            AllMigrations,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "if v_owner_id is null then",
            AllMigrations,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "where id = p_device_id and owner_id = v_owner_id",
            AllMigrations,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "where owner_id = v_owner_id and id = v_entity_id",
            AllMigrations,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "grant execute on function public.moicalendar_apply_calendar_mutation(jsonb) to anon",
            AllMigrations,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecurityDefinerFunctions_ArePinnedToTrustedSearchPath()
    {
        var securityDefinerNames = SecurityDefinerFunctionRegex()
            .Matches(AllMigrations)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(securityDefinerNames);
        Assert.All(securityDefinerNames, name => Assert.Contains(
            $"alter function public.{name}",
            HardeningMigration,
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            "set search_path = public, pg_temp",
            HardeningMigration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "set search_path = pg_catalog, pg_temp",
            HardeningMigration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "revoke create on schema public from public, anon, authenticated",
            HardeningMigration,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccountDeletion_UsesVerifiedIdentityStrictOriginAndServerOnlyRateLimit()
    {
        Assert.Contains("auth/v1/user", EdgeFunction, StringComparison.Ordinal);
        Assert.Contains("claims?.sub !== verifiedUser.id", EdgeFunction, StringComparison.Ordinal);
        Assert.Contains("claims?.role !== \"authenticated\"", EdgeFunction, StringComparison.Ordinal);
        Assert.Contains("MOICALENDAR_ALLOWED_ORIGINS", EdgeFunction, StringComparison.Ordinal);
        Assert.Contains("origin_not_allowed", EdgeFunction, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"Access-Control-Allow-Origin\": \"*\"",
            EdgeFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "moicalendar_claim_account_deletion_attempt",
            EdgeFunction,
            StringComparison.Ordinal);
        Assert.Contains("account_deletion_rate_limited", EdgeFunction, StringComparison.Ordinal);
        Assert.Contains("\"Retry-After\": \"900\"", EdgeFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("request.json", EdgeFunction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("console.log", EdgeFunction, StringComparison.OrdinalIgnoreCase);

        Assert.Matches(
            "grant execute on function public\\.moicalendar_claim_account_deletion_attempt\\(uuid\\)" +
            "\\s+to service_role",
            HardeningMigration);
        Assert.DoesNotMatch(
            "grant execute on function public\\.moicalendar_claim_account_deletion_attempt\\(uuid\\)" +
            "\\s+to authenticated",
            HardeningMigration);
    }

    [Fact]
    public void ProductionBuild_AppliesHashedBlazorCompatibleSecurityHeadersBeforeValidation()
    {
        var hardener = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "harden-production-publish.mjs"));
        var validator = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "validate-production-publish.mjs"));
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "azure-static-web-apps-polite-rock-09eddaf00.yml"));

        foreach (var directive in new[]
        {
            "default-src 'self'",
            "base-uri 'self'",
            "object-src 'none'",
            "frame-ancestors 'none'",
            "script-src 'self' 'wasm-unsafe-eval'",
            "connect-src 'self' https: wss:"
        })
        {
            Assert.Contains(directive, hardener, StringComparison.Ordinal);
            Assert.Contains(directive, validator, StringComparison.Ordinal);
        }
        Assert.Contains("createHash(\"sha256\")", hardener, StringComparison.Ordinal);
        Assert.Contains("\"X-Content-Type-Options\": \"nosniff\"", hardener, StringComparison.Ordinal);
        Assert.Contains("\"Referrer-Policy\": \"strict-origin-when-cross-origin\"", hardener, StringComparison.Ordinal);
        Assert.Contains("\"X-Frame-Options\": \"DENY\"", hardener, StringComparison.Ordinal);
        Assert.Contains("brotliCompressSync", hardener, StringComparison.Ordinal);
        Assert.Contains("gzipSync", hardener, StringComparison.Ordinal);
        Assert.Contains("brotliDecompressSync", validator, StringComparison.Ordinal);
        Assert.Contains("gunzipSync", validator, StringComparison.Ordinal);

        var serviceWorker = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "MoiCalendar.App",
            "wwwroot",
            "service-worker.published.js"));
        Assert.Contains("/^staticwebapp\\.config\\.json$/", serviceWorker, StringComparison.Ordinal);

        var publishIndex = workflow.IndexOf("dotnet publish", StringComparison.Ordinal);
        var hardenIndex = workflow.IndexOf("npm run deploy:harden", StringComparison.Ordinal);
        var validateIndex = workflow.IndexOf("npm run deploy:validate", StringComparison.Ordinal);
        Assert.True(publishIndex >= 0 && publishIndex < hardenIndex && hardenIndex < validateIndex);
    }

    [Fact]
    public void CommittedBrowserConfiguration_ContainsNoPrivilegedSecretFieldOrValue()
    {
        var configurationDirectory = Path.Combine(
            RepositoryRoot,
            "src",
            "MoiCalendar.App",
            "wwwroot");
        foreach (var path in Directory.GetFiles(configurationDirectory, "appsettings*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var serialized = document.RootElement.GetRawText();
            Assert.DoesNotContain("service_role", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sb_secret_", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("databasePassword", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("clientSecret", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("webDavPassword", serialized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex(
        @"create\s+function\s+public\.(moicalendar_[a-z0-9_]+)[\s\S]*?security\s+definer",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecurityDefinerFunctionRegex();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MoiCalendar.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }
}
