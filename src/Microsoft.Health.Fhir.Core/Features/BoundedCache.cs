// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Core.Features
{
    /// <summary>
    /// Bounded cache of values created from their key, for keys that may come from user input.
    /// </summary>
    /// <remarks>
    /// This type is thread-safe. A hit is a lock-free dictionary lookup that returns the same instance every time,
    /// so hits must not take locks or allocate. A miss creates the value under a single mutation lock, so each key is
    /// created at most once while it is cached. When an addition exceeds the capacity, the oldest entry is evicted
    /// (first in, first out). Creation failures are not cached; they propagate to the caller.
    /// </remarks>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    internal sealed class BoundedCache<TKey, TValue>
    {
        private readonly ConcurrentDictionary<TKey, TValue> _cache;
        private readonly Queue<TKey> _insertionOrder = new();
        private readonly object _mutationSync = new();
        private readonly int _capacity;
        private readonly Func<TKey, TValue> _create;

        /// <summary>
        /// Initializes a new instance of the <see cref="BoundedCache{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="capacity">The maximum number of cached values.</param>
        /// <param name="create">Creates a value that is not cached. It is called under the cache's mutation lock.</param>
        /// <param name="comparer">The key comparer, or null for the default.</param>
        public BoundedCache(int capacity, Func<TKey, TValue> create, IEqualityComparer<TKey> comparer = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            ArgumentNullException.ThrowIfNull(create);

            _capacity = capacity;
            _create = create;
            _cache = new ConcurrentDictionary<TKey, TValue>(comparer ?? EqualityComparer<TKey>.Default);
        }

        /// <summary>
        /// Returns the cached value, creating and caching it on a miss.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The value.</returns>
        public TValue GetOrAdd(TKey key)
        {
            if (_cache.TryGetValue(key, out TValue value))
            {
                return value;
            }

            lock (_mutationSync)
            {
                if (_cache.TryGetValue(key, out value))
                {
                    return value;
                }

                value = _create(key);
                if (!_cache.TryAdd(key, value))
                {
                    throw new InvalidOperationException("The bounded cache changed while holding its mutation lock.");
                }

                _insertionOrder.Enqueue(key);

                // Bound on the queue's count: ConcurrentDictionary.Count acquires every lock stripe.
                if (_insertionOrder.Count > _capacity)
                {
                    TKey oldest = _insertionOrder.Dequeue();
                    if (!_cache.TryRemove(oldest, out _))
                    {
                        throw new InvalidOperationException("The bounded cache and its eviction queue are inconsistent.");
                    }
                }

                return value;
            }
        }
    }
}
