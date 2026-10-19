// Ported from Git Credential Manager, and deliberately kept close to it: the only way to take a
// later upstream fix is to diff against it, which reformatting to this repo's house style would
// make impossible. The .editorconfig beside this project turns the rules that would rewrite it off.
//
//     https://github.com/git-ecosystem/git-credential-manager
//     src/Core/Interop/MacOS/Native/LibSystem.cs  @ 838e3496
//
// Copyright (c) GitHub, Inc. and contributors. Licensed under the MIT License.
// See THIRD-PARTY-NOTICES.md and licenses/MIT-git-credential-manager.txt.
//
// Changed from upstream: nullable disabled (upstream is unannotated), namespace.

#nullable disable
using System;
using System.Runtime.InteropServices;

namespace PoniExp.Secrets.Interop.MacOS.Native
{
    public static class LibSystem
    {
        private const string LibSystemLib = "/System/Library/Frameworks/System.framework/System";

        [DllImport(LibSystemLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr dlopen(string name, int flags);

        [DllImport(LibSystemLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr dlsym(IntPtr handle, string symbol);

        public static IntPtr GetGlobal(IntPtr handle, string symbol)
        {
            IntPtr ptr = dlsym(handle, symbol);
            return Marshal.PtrToStructure<IntPtr>(ptr);
        }
    }
}
