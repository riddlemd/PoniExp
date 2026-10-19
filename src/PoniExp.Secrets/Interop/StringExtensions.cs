// Ported from Git Credential Manager, and deliberately kept close to it: the only way to take a
// later upstream fix is to diff against it, which reformatting to this repo's house style would
// make impossible. The .editorconfig beside this project turns the rules that would rewrite it off.
//
//     https://github.com/git-ecosystem/git-credential-manager
//     src/Core/StringExtensions.cs  @ 838e3496
//
// Copyright (c) GitHub, Inc. and contributors. Licensed under the MIT License.
// See THIRD-PARTY-NOTICES.md and licenses/MIT-git-credential-manager.txt.
//
// Changed from upstream: nullable disabled (upstream is unannotated), namespace; trimmed to the
// two members the ported stores use, and their argument guards inlined.

#nullable disable

using System;

namespace PoniExp.Secrets
{
    public static class StringExtensions
    {
        public static string TrimUntilIndexOf(this string str, char c)
        {
            ArgumentNullException.ThrowIfNull(str);

            int first = str.IndexOf(c);
            if (first > -1)
            {
                return str.Substring(first + 1, str.Length - first - 1);
            }

            return str;
        }

        public static string TrimUntilIndexOf(this string str, string value, StringComparison comparisonType = StringComparison.Ordinal)
        {
            ArgumentNullException.ThrowIfNull(str);

            int first = str.IndexOf(value, comparisonType);
            if (first > -1)
            {
                return str.Substring(first + value.Length, str.Length - first - value.Length);
            }

            return str;
        }
    }
}
