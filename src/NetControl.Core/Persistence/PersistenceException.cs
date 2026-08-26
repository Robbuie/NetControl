namespace NetControl.Core.Persistence;

/// <summary>
/// Anything the project store refuses to do, or cannot.
///
/// Persistence failures are the ones most likely to be seen by someone standing in a plant with
/// a laptop on a drum, so the message names the file and the likely cause, and
/// <see cref="NetControlException.Remediation"/> names the next action.
/// </summary>
public class PersistenceException : NetControlException
{
    public PersistenceException(string message) : base(message) { }

    public PersistenceException(string message, Exception? inner) : base(message, inner) { }
}
