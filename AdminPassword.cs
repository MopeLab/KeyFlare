using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace KeyFlare;

public sealed class AdminPassword(Store store)
{
    private readonly PasswordHasher<AdminPassword> hasher = new();

    public void Initialize()
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var schema = Store.Command(db, """
            CREATE TABLE IF NOT EXISTS admin_password (
                id INTEGER PRIMARY KEY CHECK(id=1), hash TEXT NOT NULL, stamp TEXT NOT NULL
            );
            """);
        schema.Transaction = tx;
        schema.ExecuteNonQuery();
        using var existing = Store.Command(db, "SELECT 1 FROM admin_password WHERE id=1");
        existing.Transaction = tx;
        if (existing.ExecuteScalar() is not null) return;

        var password = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        using var insert = Store.Command(db, "INSERT INTO admin_password VALUES(1,$hash,$stamp)",
            ("$hash", hasher.HashPassword(this, password)), ("$stamp", Guid.NewGuid().ToString("N")));
        insert.Transaction = tx;
        insert.ExecuteNonQuery();
        tx.Commit();
        Console.WriteLine($"KeyFlare AdminPasswd: {password}");
    }

    public string? Verify(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024) return null;
        using var db = store.Open();
        using var command = Store.Command(db, "SELECT hash,stamp FROM admin_password WHERE id=1");
        using var reader = command.ExecuteReader();
        if (!reader.Read() || hasher.VerifyHashedPassword(this, reader.GetString(0), password) == PasswordVerificationResult.Failed)
            return null;
        return reader.GetString(1);
    }

    public bool IsCurrent(string? stamp)
    {
        using var db = store.Open();
        using var command = Store.Command(db, "SELECT stamp FROM admin_password WHERE id=1");
        return stamp is not null && stamp == command.ExecuteScalar() as string;
    }

    public bool Change(string? current, string replacement, string? sessionStamp)
    {
        var stamp = Verify(current);
        if (stamp is null || stamp != sessionStamp) return false;
        using var db = store.Open();
        using var command = Store.Command(db, "UPDATE admin_password SET hash=$hash,stamp=$next WHERE id=1 AND stamp=$old",
            ("$hash", hasher.HashPassword(this, replacement)), ("$next", Guid.NewGuid().ToString("N")), ("$old", stamp));
        return command.ExecuteNonQuery() == 1;
    }
}
