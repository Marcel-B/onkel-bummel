namespace OnkelBummel.Api.Data;

/// <summary>
/// Full-text search over title, body and tags of the messages: an FTS5 table that only holds the index and reads the
/// text from <c>Messages</c> (external content), kept current by triggers.
/// </summary>
public static class MessageSearch
{
    public const string Table = "MessagesFts";

    // unicode61 with remove_diacritics finds "Tür" with "tur"; prefix indexes make "dru*" fast.
    public const string Create = """
        CREATE VIRTUAL TABLE "MessagesFts" USING fts5(
            "Title", "Body", "Tags",
            content='Messages', content_rowid='Id',
            tokenize='unicode61 remove_diacritics 2', prefix='2 3');

        CREATE TRIGGER "Messages_fts_insert" AFTER INSERT ON "Messages" BEGIN
            INSERT INTO "MessagesFts"(rowid, "Title", "Body", "Tags") VALUES (new."Id", new."Title", new."Body", new."Tags");
        END;

        CREATE TRIGGER "Messages_fts_delete" AFTER DELETE ON "Messages" BEGIN
            INSERT INTO "MessagesFts"("MessagesFts", rowid, "Title", "Body", "Tags") VALUES ('delete', old."Id", old."Title", old."Body", old."Tags");
        END;

        CREATE TRIGGER "Messages_fts_update" AFTER UPDATE OF "Title", "Body", "Tags" ON "Messages" BEGIN
            INSERT INTO "MessagesFts"("MessagesFts", rowid, "Title", "Body", "Tags") VALUES ('delete', old."Id", old."Title", old."Body", old."Tags");
            INSERT INTO "MessagesFts"(rowid, "Title", "Body", "Tags") VALUES (new."Id", new."Title", new."Body", new."Tags");
        END;
        """;

    public const string Drop = """
        DROP TRIGGER IF EXISTS "Messages_fts_update";
        DROP TRIGGER IF EXISTS "Messages_fts_delete";
        DROP TRIGGER IF EXISTS "Messages_fts_insert";
        DROP TABLE IF EXISTS "MessagesFts";
        """;

    /// <summary>
    /// Turns what a user types into an FTS5 query: every word must occur, as a prefix ("drucker fert" finds
    /// "Drucker fertig"). Quotes keep FTS5 syntax such as AND, NEAR or "-" from being interpreted.
    /// </summary>
    public static string? ToQuery(string? input)
    {
        var words = (input ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Replace("\"", "", StringComparison.Ordinal))
            // A word without letters or digits gives the tokenizer nothing, and an empty phrase is a syntax error.
            .Where(w => w.Any(char.IsLetterOrDigit))
            .Select(w => $"\"{w}\"*")
            .ToList();
        return words.Count == 0 ? null : string.Join(' ', words);
    }
}
