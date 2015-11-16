﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Async.Model.Sequence;

namespace Async.Model.AsyncLoaded
{
    public sealed class ThreadSafeAsyncLoader<TItem> : AsyncLoader<TItem>
    {

        /// <summary>
        /// An update comparer that deems any item updated by always returning false. Necessary
        /// because we cannot in general know if two items that test equal are in fact identical.
        /// By doing this we choose correctness over performance: we may notify about updates where
        /// none occurred, but at least we will never miss an update.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        class ConservativeUpdateComparer<T> : EqualityComparer<T>
        {
            public override bool Equals(T x, T y)
            {
                return false;
            }

            public override int GetHashCode(T obj)
            {
                return obj.GetHashCode();
            }
        }

        private static readonly ConservativeUpdateComparer<TItem> conservativeUpdateComparer = new ConservativeUpdateComparer<TItem>();

        public ThreadSafeAsyncLoader(
            Func<IEnumerable<TItem>, ISeq<TItem>> seqFactory,
            Func<CancellationToken, Task<IEnumerable<TItem>>> loadDataAsync = null,
            Func<IEnumerable<TItem>, CancellationToken, Task<IEnumerable<ItemChange<TItem>>>> fetchUpdatesAsync = null,
            CancellationToken rootCancellationToken = default(CancellationToken),
            SynchronizationContext eventContext = null) : base(seqFactory, loadDataAsync, fetchUpdatesAsync, rootCancellationToken, eventContext)
        {
            // Do nothing: base constructor handles everything
        }

        // TODO: Move all event notification out of the lock scope to minimize contention

        public override TItem Take()
        {
            using (mutex.Lock())
            {
                return base.Take();
            }
        }

        public override void Conj(TItem item)
        {
            using (mutex.Lock())
            {
                base.Conj(item);
            }
        }

        public override void Replace(TItem oldItem, TItem newItem)
        {
            var comparer = EqualityComparer<TItem>.Default;
            ItemChange<TItem>[] changes;

            using (mutex.Lock())
            {
                // NOTE: Cannot use LinqExtensions.Replace here, since we need to know which items
                // were replaced for event notifications
                changes = seq.Select(item =>
                {
                    if (comparer.Equals(item, oldItem))
                        return new ItemChange<TItem>(ChangeType.Updated, newItem);
                    else
                        return new ItemChange<TItem>(ChangeType.Unchanged, item);
                }).ToArray();

                // Perform replacement
                seq.ReplaceAll(changes.Select(c => c.Item));
            }

            NotifyCollectionChanged(changes.Where(c => c.Type == ChangeType.Updated).ToArray());
        }

        public override void ReplaceAll(IEnumerable<TItem> newItems)
        {
            ItemChange<TItem>[] changes;

            using (mutex.Lock())
            {
                // Be conservative when checking for updated items
                // NOTE: This will generate updates for unchanged items, which should not be a correctness problem
                changes = newItems.ChangesFrom(seq, updateComparer: conservativeUpdateComparer)
                    .ToArray();  // must materialize before we change seq

                seq.ReplaceAll(newItems);
            }

            NotifyCollectionChanged(changes);
        }

        public override void Clear()
        {
            ItemChange<TItem>[] changes;

            using (mutex.Lock())
            {
                changes = seq.Select(item => new ItemChange<TItem>(ChangeType.Removed, item))
                    .ToArray();  // must materialize before we change seq

                seq.Clear();
            }

            NotifyCollectionChanged(changes);
        }

        public override IEnumerator<TItem> GetEnumerator()
        {
            using (mutex.Lock())
            {
                // Take a snapshot under lock and return an enumerator of the snapshot
                return seq.ToList().GetEnumerator();
            }
        }

        public override Task<TItem> TakeAsync(CancellationToken cancellationToken)
        {
            // TODO: Confirm that it doesn't make sense
            throw new NotSupportedException("Does not make sense for a locking collection");
        }

        public override Task ConjAsync(TItem item, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Does not make sense for a locking collection");
        }
    }
}
