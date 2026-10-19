// Ported from Git Credential Manager, and deliberately kept close to it: the only way to take a
// later upstream fix is to diff against it, which reformatting to this repo's house style would
// make impossible. The .editorconfig beside this project turns the rules that would rewrite it off.
//
//     https://github.com/git-ecosystem/git-credential-manager
//     src/Core/Interop/InteropException.cs  @ 838e3496
//
// Copyright (c) GitHub, Inc. and contributors. Licensed under the MIT License.
// See THIRD-PARTY-NOTICES.md and licenses/MIT-git-credential-manager.txt.
//
// Changed from upstream: nullable disabled (upstream is unannotated), namespace.

#nullable disable
using System;
using System.ComponentModel;
using System.Diagnostics;

namespace PoniExp.Secrets.Interop
{
    /// <summary>
    /// An unexpected error occurred in interop-code.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplay}")]
    public class InteropException : Exception
    {
        public InteropException()
            : base() { }

        public InteropException(string message, int errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public InteropException(string message, int errorCode, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }

        public InteropException(string message, Win32Exception w32Exception)
            : base(message, w32Exception)
        {
            ErrorCode = w32Exception.NativeErrorCode;
        }

        /// <summary>
        /// Native error code.
        /// </summary>
        public int ErrorCode { get; }

        private string DebuggerDisplay => $"{Message} [0x{ErrorCode:x}]";
    }
}
