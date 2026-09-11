using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Glosify.Services.Ai;

/// <summary>Read-only release checks. Runs before web-host initialization on the deploy runner.</summary>
public static class CreditLedgerDeploymentCheck
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[1] is not ("drained" or "fingerprint" or "balance"))
            throw new ArgumentException("Use --credit-ledger-check drained|fingerprint|balance.");
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException("A deployment SQL connection is required.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        if (args[1] == "drained")
        {
            // Older abandoned reservations remain preserved; they cannot represent
            // an active provider call after the bounded drain and worker stop.
            command.CommandText = """
                SELECT COUNT_BIG(*) FROM AiCreditTransactions r
                WHERE r.Kind = 'reservation' AND r.CreatedAt > DATEADD(minute, -10, SYSUTCDATETIME())
                AND NOT EXISTS (SELECT 1 FROM AiCreditTransactions t
                    WHERE t.ReservationId = r.ReservationId AND t.Kind IN ('usage_debit', 'release'))
                """;
            return (long)(await command.ExecuteScalarAsync())! == 0 ? 0 : 2;
        }
        if (args[1] == "balance")
        {
            // This mode is restricted to the explicitly configured deployment smoke account.
            command.CommandText = """
                SELECT a.BalanceCredits FROM AiCreditAccounts a
                JOIN AspNetUsers u ON u.Id = a.UserId WHERE u.NormalizedEmail = @email
                """;
            command.Parameters.AddWithValue("@email", (Environment.GetEnvironmentVariable("CreditSmoke__Email")
                ?? throw new InvalidOperationException("A smoke account email is required.")).ToUpperInvariant());
            var balance = await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Smoke account not found.");
            Console.WriteLine(Convert.ToDecimal(balance, CultureInfo.InvariantCulture).ToString("F6", CultureInfo.InvariantCulture));
            return 0;
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Canonical decimal casts produce identical bytes before and after migration.
        // Include every key and financial value, not just aggregate sums.
        command.CommandText = """
            SELECT UserId, CAST(BalanceCredits AS decimal(19,6)), CAST(ReservedCredits AS decimal(19,6))
            FROM AiCreditAccounts ORDER BY UserId;
            SELECT CONVERT(nvarchar(36), Id), CAST(CreditAmount AS decimal(19,6)),
                CAST(BalanceAfterCredits AS decimal(19,6)), CAST(ReservedAfterCredits AS decimal(19,6))
            FROM AiCreditTransactions ORDER BY Id;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            hash.AppendData([0xff]);
            while (await reader.ReadAsync())
            {
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = index == 0 ? reader.GetString(index)
                        : reader.GetDecimal(index).ToString("F6", CultureInfo.InvariantCulture);
                    var bytes = Encoding.UTF8.GetBytes(value);
                    hash.AppendData(BitConverter.GetBytes(bytes.Length));
                    hash.AppendData(bytes);
                }
            }
        } while (await reader.NextResultAsync());
        Console.WriteLine(Convert.ToHexString(hash.GetHashAndReset()));
        return 0;
    }
}
