using System;

namespace WPFSharp.Globalizer.Exceptions
{
    [Serializable]
    public class StyleNotFoundException : ArgumentException
    {
        public StyleNotFoundException() : base() { }

        public StyleNotFoundException(string message) : base(message) { }

        public StyleNotFoundException(string message, Exception innerException) : base(message, innerException) { }

        public StyleNotFoundException(string message, string invalidStyleName, Exception innerException) : base(message, innerException)
        {
            InvalidStyleName = invalidStyleName;
        }

        public virtual string InvalidStyleName { get; private set; } = string.Empty;
    }
}
