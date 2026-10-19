// Ported from Git Credential Manager, and deliberately kept close to it: the only way to take a
// later upstream fix is to diff against it, which reformatting to this repo's house style would
// make impossible. The .editorconfig beside this project turns the rules that would rewrite it off.
//
//     https://github.com/git-ecosystem/git-credential-manager
//     src/Core/Interop/MacOS/MacOSKeychainCredential.cs  @ 838e3496
//
// Copyright (c) GitHub, Inc. and contributors. Licensed under the MIT License.
// See THIRD-PARTY-NOTICES.md and licenses/MIT-git-credential-manager.txt.
//
// Changed from upstream: nullable disabled (upstream is unannotated), namespace.

#nullable disable
using System.Diagnostics;

namespace PoniExp.Secrets.Interop.MacOS
{
    [DebuggerDisplay("{DebuggerDisplay}")]
    public class MacOSKeychainCredential : ICredential
    {
        internal MacOSKeychainCredential(string service, string account, string password, string label)
        {
            Service = service;
            Account = account;
            Password = password;
            Label = label;
        }

        public string Service  { get; }

        public string Account { get; }

        public string Label { get; }

        public string Password { get; }

        private string DebuggerDisplay => $"{Label} [Service: {Service}, Account: {Account}]";
    }
}
