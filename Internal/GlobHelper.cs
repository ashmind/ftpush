﻿using System.Text.RegularExpressions;

namespace Ftpush.Internal {
    public static class GlobHelper {
        public static Regex ConvertGlobsToRegex(string pattern) {
            var converted = Regex.Replace(pattern, @"\*\*|[\\/\.+*?\[\]{}()^$]", m => {
                if (m.Value == @"\" || m.Value == "/")
                    return @"[\\/]";

                if (m.Value == "**")
                    return ".*";

                if (m.Value == "*")
                    return "[^\\/]*";

                if (m.Value == "?")
                    return "[^\\/]";

                return @"\" + m.Value;
            });
            converted = @"(?:^|[\\/\.])" + converted + @"(?:[\\/\.]|$)";
            return new Regex(converted, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
