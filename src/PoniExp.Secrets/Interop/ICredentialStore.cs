// Ported from Git Credential Manager, and deliberately kept close to it: the only way to take a
// later upstream fix is to diff against it, which reformatting to this repo's house style would
// make impossible. The .editorconfig beside this project turns the rules that would rewrite it off.
//
//     https://github.com/git-ecosystem/git-credential-manager
//     src/Core/ICredentialStore.cs  @ 838e3496
//
// Copyright (c) GitHub, Inc. and contributors. Licensed under the MIT License.
// See THIRD-PARTY-NOTICES.md and licenses/MIT-git-credential-manager.txt.
//
// Changed from upstream: nullable disabled (upstream is unannotated), namespace.

#nullable disable
using System.Collections.Generic;

namespace PoniExp.Secrets
{
    /// <summary>
    /// Represents a secure storage location for <see cref="ICredential"/>s.
    /// </summary>
    public interface ICredentialStore
    {
        /// <summary>
        /// Get the name of the credential store.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Get all accounts from the store for the given service.
        /// </summary>
        /// <param name="service">Name of the service to match against. Use null to match all values.</param>
        /// <returns>All accounts that match the query.</returns>
        IList<string> GetAccounts(string service);

        /// <summary>
        /// Get the first credential from the store that matches the given query.
        /// </summary>
        /// <param name="service">Name of the service to match against. Use null to match all values.</param>
        /// <param name="account">Account name to match against. Use null to match all values.</param>
        /// <returns>First matching credential or null if none are found.</returns>
        ICredential Get(string service, string account);

        /// <summary>
        /// Add or update credential in the store with the specified key.
        /// </summary>
        /// <param name="service">Name of the service this credential is for. Use null to match all values.</param>
        /// <param name="account">Account associated with this credential. Use null to match all values.</param>
        /// <param name="secret">Secret value to store.</param>
        void AddOrUpdate(string service, string account, string secret);

        /// <summary>
        /// Delete credential from the store that matches the given query.
        /// </summary>
        /// <param name="service">Name of the service to match against. Use null to match all values.</param>
        /// <param name="account">Account name to match against. Use null to match all values.</param>
        /// <returns>True if the credential was deleted, false otherwise.</returns>
        bool Remove(string service, string account);
    }
}
