namespace FeedCord.Common
{
    /// <summary>
    /// The identities of posts already handled for one feed, bounded and held in
    /// first-seen order.
    ///
    /// This is what makes "is this post new?" independent of the clock. The only
    /// state kept used to be a high-water publish date, so a feed that hands us
    /// an item hours after its stated publish time - or two items sharing one
    /// timestamp - lost posts permanently.
    /// </summary>
    public sealed class SeenPostSet
    {
        /// <summary>
        /// Comfortably larger than any realistic feed window, so an entry is
        /// only ever evicted long after the item it names has dropped out of the
        /// document and can no longer be re-offered.
        /// </summary>
        public const int Capacity = 500;

        private readonly object _gate = new();
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        public SeenPostSet()
        {
        }

        public SeenPostSet(IEnumerable<string> initial)
        {
            foreach (var id in initial)
            {
                Add(id);
            }
        }

        /// <summary>
        /// True once eviction has begun -- that is, once this set can no longer
        /// be trusted to remember every post it has been told about.
        /// </summary>
        public bool IsFull
        {
            get
            {
                lock (_gate)
                {
                    return _order.Count >= Capacity;
                }
            }
        }

        public bool Contains(string id)
        {
            lock (_gate)
            {
                return _ids.Contains(id);
            }
        }

        public void Add(string id)
        {
            if (string.IsNullOrEmpty(id))
                return;

            lock (_gate)
            {
                // Only enqueue on a genuinely new id, so _order and _ids stay
                // one-to-one and eviction cannot drop a live entry.
                if (!_ids.Add(id))
                    return;

                _order.Enqueue(id);

                while (_order.Count > Capacity)
                {
                    _ids.Remove(_order.Dequeue());
                }
            }
        }

        /// <summary>
        /// Copied under the lock: OnShutdown fires from ApplicationStopping and
        /// can land while a check cycle is still adding to the set.
        /// </summary>
        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
            {
                return _order.ToArray();
            }
        }
    }
}
