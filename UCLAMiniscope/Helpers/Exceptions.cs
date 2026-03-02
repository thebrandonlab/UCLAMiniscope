/*
Description:
  Custom exceptions for UCLA Miniscope operations.

Author:
  Clément Bourguignon
  Brandon Lab @ McGill University
  2025

MIT License
Copyright (c) 2024 Clément Bourguignon
*/

using System;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Exception thrown when a miniscope device cannot be found or opened.
    /// </summary>
    [Serializable]
    public class NoMiniscopeException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NoMiniscopeException"/> class.
        /// </summary>
        public NoMiniscopeException() : base() { }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="NoMiniscopeException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public NoMiniscopeException(string message) : base(message) { }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="NoMiniscopeException"/> class with a specified error message and inner exception.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="inner">The exception that is the cause of the current exception.</param>
        public NoMiniscopeException(string message, Exception inner) : base(message, inner) { }
    }
}
