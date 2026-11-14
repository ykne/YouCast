using System;

namespace Service
{
    public sealed class FeedNotFoundException : Exception
    {
        public FeedNotFoundException(string message) : base(message)
        {
        }
    }
}
