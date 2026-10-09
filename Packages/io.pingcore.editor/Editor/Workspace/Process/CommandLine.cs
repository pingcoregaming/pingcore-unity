using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PingCore.Editor.Workspace.Process
{
    /// <summary>Windows command-line quoting (the <c>CommandLineToArgvW</c> rules). Pure.</summary>
    public static class CommandLine
    {
        /// <summary>Joins arguments into one command line that parses back to exactly them.</summary>
        public static string Join(IEnumerable<string> args)
        {
            return string.Join(" ", (args ?? Enumerable.Empty<string>()).Select(Quote));
        }

        /// <summary>Quotes one argument when it needs it.</summary>
        public static string Quote(string arg)
        {
            if (arg == null)
            {
                throw new ArgumentNullException(nameof(arg));
            }

            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return arg;
            }

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }

                backslashes = 0;
                sb.Append(c);
            }

            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
