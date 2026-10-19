// Ported from Git Credential Manager, and deliberately kept close to it: the only way to take a
// later upstream fix is to diff against it, which reformatting to this repo's house style would
// make impossible. The .editorconfig beside this project turns the rules that would rewrite it off.
//
//     https://github.com/git-ecosystem/git-credential-manager
//     src/Core/Interop/InteropUtils.cs  @ 838e3496
//
// Copyright (c) GitHub, Inc. and contributors. Licensed under the MIT License.
// See THIRD-PARTY-NOTICES.md and licenses/MIT-git-credential-manager.txt.
//
// Changed from upstream: nullable disabled (upstream is unannotated), namespace.

#nullable disable
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace PoniExp.Secrets.Interop
{
    internal static class InteropUtils
    {
        public static byte[] ToByteArray(IntPtr ptr, long count)
        {
            var destination = new byte[count];
            Marshal.Copy(ptr, destination, 0, destination.Length);
            return destination;
        }

        public static bool AreEqual(byte[] bytes, IntPtr ptr, uint length)
        {
            if (bytes.Length == 0 && (ptr == IntPtr.Zero || length == 0))
            {
                return true;
            }

            if (bytes.Length != length)
            {
                return false;
            }

            byte[] ptrBytes = ToByteArray(ptr, length);
            return bytes.SequenceEqual(ptrBytes);
        }
    }
}
