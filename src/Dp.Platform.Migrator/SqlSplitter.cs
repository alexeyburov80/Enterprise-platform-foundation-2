using System.Text;

namespace Dp.Platform.Migrator;

/// <summary>
/// Разделение SQL-скрипта на отдельные операторы (SRP: только лексика SQL).
/// Учитывает $$-долларовые кавычки (тела plpgsql-функций), строковые литералы
/// и комментарии — ';' внутри них оператором не считается.
/// </summary>
public static class SqlSplitter
{
    public static IReadOnlyList<string> Split(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            // Долларовые кавычки: $$...$$ или $tag$...$tag$ (тела функций).
            if (c == '$')
            {
                var j = i + 1;
                while (j < sql.Length && (char.IsAsciiLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                if (j < sql.Length && sql[j] == '$')
                {
                    var tag = sql[i..(j + 1)];
                    var end = sql.IndexOf(tag, j + 1, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        current.Append(sql[i..(end + tag.Length)]);
                        i = end + tag.Length;
                        continue;
                    }
                }
            }

            // Строковые литералы с экранированием ''.
            if (c == '\'')
            {
                current.Append(sql[i++]);
                while (i < sql.Length)
                {
                    current.Append(sql[i]);
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { current.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            // Комментарии до конца строки.
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end + 1;
                current.Append(sql[i..end]);
                i = end;
                continue;
            }

            if (c == ';')
            {
                Add(statements, current);
                i++;
                continue;
            }

            current.Append(sql[i++]);
        }

        Add(statements, current);
        return statements;
    }

    private static void Add(List<string> statements, StringBuilder current)
    {
        var text = current.ToString().Trim();
        if (text.Length > 0) statements.Add(text);
        current.Clear();
    }
}
