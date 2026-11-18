namespace WinterOutbreak
{
    internal sealed class Fixed<T>
    {
        internal Fixed(T value)
        {
            Value = value;
        }

        internal T Value { get; }
    }
}
