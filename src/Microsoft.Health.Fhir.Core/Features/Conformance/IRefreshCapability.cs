// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Health.Fhir.Core.Features.Conformance
{
    /// <summary>
    /// A capability provider whose contribution can change while the server runs. It is refreshed each time the
    /// CapabilityStatement is periodically rebuilt, after <see cref="IProvideCapability.BuildAsync"/> ran once.
    /// </summary>
    public interface IRefreshCapability : IProvideCapability
    {
        /// <summary>
        /// Re-applies the parts of this provider's contribution that can change. Must be idempotent.
        /// </summary>
        /// <param name="builder">Instance of the CapabilityStatementBuilder.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task RefreshAsync(ICapabilityStatementBuilder builder, CancellationToken cancellationToken);
    }
}
