using System.Text;

namespace ChromiumProcessExplorer.Gui;

public static class PropertyFilter
{
    public static bool Matches(
        string filter,
        params (string Name, string? Value)[] properties)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(properties);
        string[] tokens = Tokenize(filter);
        if (tokens.Length == 0)
        {
            return true;
        }

        return tokens.All(token =>
        {
            int separator = token.IndexOf(':');
            if (separator > 0)
            {
                string name = token[..separator];
                string value = Unquote(token[(separator + 1)..]);
                (string Name, string? Value)[] matchingProperties = properties
                    .Where(property => property.Name.Equals(
                        name,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matchingProperties.Length > 0)
                {
                    return matchingProperties.Any(property =>
                        Contains(property.Value, value));
                }
            }

            string text = Unquote(token);
            return properties.Any(property => Contains(property.Value, text));
        });
    }

    private static bool Contains(string? value, string filter)
    {
        return value?.Contains(
            filter,
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string Unquote(string value)
    {
        return value.Trim().Trim('"');
    }

    private static string[] Tokenize(string value)
    {
        List<string> tokens = [];
        StringBuilder token = new();
        bool quoted = false;
        foreach (char character in value.Trim())
        {
            if (character == '"')
            {
                quoted = !quoted;
                token.Append(character);
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                AddToken();
            }
            else
            {
                token.Append(character);
            }
        }

        AddToken();
        return tokens.ToArray();

        void AddToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            tokens.Add(token.ToString());
            token.Clear();
        }
    }
}
