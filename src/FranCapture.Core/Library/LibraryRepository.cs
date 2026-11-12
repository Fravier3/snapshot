using Microsoft.Data.Sqlite;

namespace FranCapture.Core.Library;

public enum MediaKind { Image = 0, Video = 1, Gif = 2 }

public sealed class LibraryItem
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public MediaKind Kind { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public bool Favorite { get; set; }
    public string Tags { get; set; } = "";        // separadas por coma
    public string Collection { get; set; } = "";
    public string Name => FranCapture.Core.Storage.FileNaming.NameOf(Path);

    public static MediaKind KindFromPath(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".webm" or ".mkv" or ".mov" or ".avi" => MediaKind.Video,
        ".gif" => MediaKind.Gif,
        _ => MediaKind.Image,
    };
}

public sealed class LibraryQuery
{
    public string? Search { get; set; }
    public MediaKind? Kind { get; set; }
    public bool FavoritesOnly { get; set; }
    public string? Collection { get; set; }
    public string? Tag { get; set; }
    public bool NewestFirst { get; set; } = true;
    public int Limit { get; set; } = 5000;
}

/// <summary>
/// Metadatos de la biblioteca en SQLite, separados de los archivos originales.
/// La base de datos solo guarda referencias: eliminar un registro nunca borra el archivo.
/// </summary>
public sealed class LibraryRepository : IDisposable
{
    readonly SqliteConnection _db;
    readonly object _lock = new();

    public LibraryRepository(string dbPath)
    {
        if (dbPath != ":memory:") Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dbPath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS items(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              path TEXT NOT NULL UNIQUE COLLATE NOCASE,
              kind INTEGER NOT NULL,
              created_utc INTEGER NOT NULL,
              width INTEGER NOT NULL DEFAULT 0,
              height INTEGER NOT NULL DEFAULT 0,
              size_bytes INTEGER NOT NULL DEFAULT 0,
              favorite INTEGER NOT NULL DEFAULT 0,
              tags TEXT NOT NULL DEFAULT '',
              collection TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS ix_items_created ON items(created_utc);
            """);
    }

    void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    SqliteCommand Cmd(string sql, params (string, object?)[] ps)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return c;
    }

    /// <summary>Inserta o actualiza (por ruta) y devuelve el elemento con su Id.</summary>
    public LibraryItem Upsert(LibraryItem item)
    {
        lock (_lock)
        {
            using var c = Cmd("""
                INSERT INTO items(path,kind,created_utc,width,height,size_bytes,favorite,tags,collection)
                VALUES($p,$k,$c,$w,$h,$s,$f,$t,$col)
                ON CONFLICT(path) DO UPDATE SET kind=$k,width=$w,height=$h,size_bytes=$s
                RETURNING id;
                """,
                ("$p", item.Path), ("$k", (int)item.Kind), ("$c", item.CreatedUtc.Ticks), ("$w", item.Width), ("$h", item.Height),
                ("$s", item.SizeBytes), ("$f", item.Favorite ? 1 : 0), ("$t", NormalizeTags(item.Tags)), ("$col", item.Collection ?? ""));
            item.Id = (long)c.ExecuteScalar()!;
            return GetById(item.Id) ?? item;
        }
    }

    static LibraryItem Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Path = r.GetString(1), Kind = (MediaKind)r.GetInt32(2), CreatedUtc = new DateTime(r.GetInt64(3), DateTimeKind.Utc),
        Width = r.GetInt32(4), Height = r.GetInt32(5), SizeBytes = r.GetInt64(6), Favorite = r.GetInt32(7) != 0, Tags = r.GetString(8), Collection = r.GetString(9),
    };

    const string Cols = "id,path,kind,created_utc,width,height,size_bytes,favorite,tags,collection";

    public LibraryItem? GetById(long id)
    {
        lock (_lock)
        {
            using var c = Cmd($"SELECT {Cols} FROM items WHERE id=$id", ("$id", id));
            using var r = c.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    public LibraryItem? GetByPath(string path)
    {
        lock (_lock)
        {
            using var c = Cmd($"SELECT {Cols} FROM items WHERE path=$p", ("$p", path));
            using var r = c.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    public List<LibraryItem> Query(LibraryQuery q)
    {
        var where = new List<string>();
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            where.Add("(path LIKE $s ESCAPE '\\' OR tags LIKE $s ESCAPE '\\')");
            ps.Add(("$s", "%" + q.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
        }
        if (q.Kind is { } k) { where.Add("kind=$k"); ps.Add(("$k", (int)k)); }
        if (q.FavoritesOnly) where.Add("favorite=1");
        if (!string.IsNullOrEmpty(q.Collection)) { where.Add("collection=$col"); ps.Add(("$col", q.Collection)); }
        if (!string.IsNullOrEmpty(q.Tag)) { where.Add("(',' || tags || ',') LIKE $tag"); ps.Add(("$tag", "%," + q.Tag.Trim().ToLowerInvariant() + ",%")); }
        var sql = $"SELECT {Cols} FROM items" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                  + $" ORDER BY created_utc {(q.NewestFirst ? "DESC" : "ASC")}, id {(q.NewestFirst ? "DESC" : "ASC")} LIMIT {Math.Max(1, q.Limit)}";
        var list = new List<LibraryItem>();
        lock (_lock)
        {
            using var c = Cmd(sql, ps.ToArray());
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var it = Read(r);
                // La búsqueda por nombre no debe coincidir con carpetas de la ruta.
                if (!string.IsNullOrWhiteSpace(q.Search) && !it.Name.Contains(q.Search.Trim(), StringComparison.OrdinalIgnoreCase)
                    && !it.Tags.Contains(q.Search.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(it);
            }
        }
        return list;
    }

    public void SetFavorite(long id, bool fav) => Run("UPDATE items SET favorite=$f WHERE id=$id", ("$f", fav ? 1 : 0), ("$id", id));
    public void SetTags(long id, string tags) => Run("UPDATE items SET tags=$t WHERE id=$id", ("$t", NormalizeTags(tags)), ("$id", id));
    public void SetCollection(long id, string collection) => Run("UPDATE items SET collection=$c WHERE id=$id", ("$c", collection?.Trim() ?? ""), ("$id", id));
    public void UpdatePath(long id, string newPath) => Run("UPDATE items SET path=$p WHERE id=$id", ("$p", newPath), ("$id", id));
    /// <summary>Quita el registro de la biblioteca. No toca el archivo.</summary>
    public void Remove(long id) => Run("DELETE FROM items WHERE id=$id", ("$id", id));
    public void Clear() => Run("DELETE FROM items");

    void Run(string sql, params (string, object?)[] ps)
    {
        lock (_lock) { using var c = Cmd(sql, ps); c.ExecuteNonQuery(); }
    }

    public List<string> Collections()
    {
        var list = new List<string>();
        lock (_lock)
        {
            using var c = Cmd("SELECT DISTINCT collection FROM items WHERE collection<>'' ORDER BY collection");
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        return list;
    }

    public List<string> AllTags()
    {
        var set = new SortedSet<string>();
        lock (_lock)
        {
            using var c = Cmd("SELECT tags FROM items WHERE tags<>''");
            using var r = c.ExecuteReader();
            while (r.Read()) foreach (var t in r.GetString(0).Split(',', StringSplitOptions.RemoveEmptyEntries)) set.Add(t);
        }
        return set.ToList();
    }

    public long TotalSize()
    {
        lock (_lock) { using var c = Cmd("SELECT COALESCE(SUM(size_bytes),0) FROM items"); return (long)c.ExecuteScalar()!; }
    }

    public int Count()
    {
        lock (_lock) { using var c = Cmd("SELECT COUNT(*) FROM items"); return (int)(long)c.ExecuteScalar()!; }
    }

    /// <summary>Elimina de la base de datos los registros cuyo archivo ya no existe. Devuelve cuántos se quitaron.</summary>
    public int PruneMissing(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var missing = new List<long>();
        lock (_lock)
        {
            using var c = Cmd("SELECT id,path FROM items");
            using var r = c.ExecuteReader();
            while (r.Read()) if (!exists(r.GetString(1))) missing.Add(r.GetInt64(0));
        }
        foreach (var id in missing) Remove(id);
        return missing.Count;
    }

    public static string NormalizeTags(string? tags) =>
        string.Join(",", (tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).Distinct());

    public void Dispose() => _db.Dispose();
}
