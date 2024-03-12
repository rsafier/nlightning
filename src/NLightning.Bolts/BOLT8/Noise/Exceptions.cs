namespace NLightning.Bolts.BOLT8.Noise;

internal static class Exceptions
{
	public static void ThrowIfNull(object value, string name)
	{
		if (value == null)
		{
			throw new ArgumentNullException(name);
		}
	}

	public static void ThrowIfDisposed(bool disposed, string name)
	{
		if (!disposed)
		{
			return;
		}

		throw new ObjectDisposedException(name);
	}
}