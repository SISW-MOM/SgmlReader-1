/*
 * Copyright (c) 2020 Microsoft Corporation. All rights reserved.
 * Modified work Copyright (c) 2008 MindTouch. All rights reserved. 
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
#if WINDOWS_DESKTOP
using System.Runtime.Serialization;
using System.Security.Permissions;
#endif
using System.Text;
using System.Xml;

namespace Sgml {
    /// <summary>
    /// Thrown if any errors occur while parsing the source.
    /// </summary>
#if WINDOWS_DESKTOP
    [Serializable]
#endif
    public class SgmlParseException : Exception
    {
        private readonly string m_entityContext;

        /// <summary>
        /// Instantiates a new instance of SgmlParseException with no specific error information.
        /// </summary>
        public SgmlParseException()
        {
        }

        /// <summary>
        /// Instantiates a new instance of SgmlParseException with an error message describing the problem.
        /// </summary>
        /// <param name="message">A message describing the error that occurred</param>
        public SgmlParseException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Instantiates a new instance of SgmlParseException with an error message describing the problem.
        /// </summary>
        /// <param name="message">A message describing the error that occurred</param>
        /// <param name="e">The entity on which the error occurred.</param>
        public SgmlParseException(string message, Entity e)
            : base(message)
        {
            if (e != null)
                m_entityContext = e.Context();
        }

        /// <summary>
        /// Instantiates a new instance of SgmlParseException with an error message describing the problem.
        /// </summary>
        /// <param name="message">A message describing the error that occurred</param>
        /// <param name="innerException">The original exception that caused the problem.</param>
        public SgmlParseException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

#if WINDOWS_DESKTOP
        /// <summary>
        /// Initializes a new instance of the SgmlParseException class with serialized data. 
        /// </summary>
        /// <param name="streamInfo">The object that holds the serialized object data.</param>
        /// <param name="streamCtx">The contextual information about the source or destination.</param>
        protected SgmlParseException(SerializationInfo streamInfo, StreamingContext streamCtx)
            : base(streamInfo, streamCtx)
        {
            if (streamInfo != null)
                m_entityContext = streamInfo.GetString("entityContext");
        }
#endif

        /// <summary>
        /// Contextual information detailing the entity on which the error occurred.
        /// </summary>
        public string EntityContext
        {
            get
            {
                return m_entityContext;
            }
        }

#if WINDOWS_DESKTOP
        /// <summary>
        /// Populates a SerializationInfo with the data needed to serialize the exception.
        /// </summary>
        /// <param name="info">The <see cref="SerializationInfo"/> to populate with data. </param>
        /// <param name="context">The destination (see <see cref="StreamingContext"/>) for this serialization.</param>
        [SecurityPermission(SecurityAction.Demand, SerializationFormatter=true)]
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info is null)
                throw new ArgumentNullException(nameof(info));

            info.AddValue("entityContext", m_entityContext);
            base.GetObjectData(info, context);
        }
#endif
    }

    /// <summary>
    /// The different types of literal text returned by the SgmlParser.
    /// </summary>
    public enum LiteralType
    {
        /// <summary>
        /// CDATA text literals.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        CDATA,

        /// <summary>
        /// SDATA entities.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        SDATA,

        /// <summary>
        /// The contents of a Processing Instruction.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        PI
    };

    /// <summary>
    /// An Entity declared in a DTD.
    /// </summary>
    public class Entity : IDisposable
    {
        /// <summary>
        /// The character indicating End Of File.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "The capitalisation is correct since EOF is an acronym.")]
        public const char EOF = (char)65535;

        private readonly string m_name;
        private readonly bool m_isInternal;
        private readonly string m_publicId;
        private readonly string m_uri;
        private readonly string m_literal;
        private LiteralType m_literalType;
        private Entity m_parent;
        private bool m_isHtml;
        private int m_line;
        private char m_lastchar;
        private bool m_isWhitespace;
        private readonly IEntityResolver m_resolver;

        private Encoding m_encoding;
        private Uri m_resolvedUri;
        private TextReader m_stm;
        private bool m_weOwnTheStream;
        private int m_lineStart;
        private int m_absolutePos;

        /// <summary>
        /// Initialises a new instance of an Entity declared in a DTD.
        /// </summary>
        /// <param name="name">The name of the entity.</param>
        /// <param name="pubid">The public id of the entity.</param>
        /// <param name="uri">The uri of the entity.</param>
        public Entity(string name, string pubid, string uri, IEntityResolver resolver)
        {
            m_name = name;
            m_publicId = pubid;
            m_uri = uri;
            m_isHtml = (name != null && StringUtilities.EqualsIgnoreCase(name, "html"));
            m_resolver = resolver;
        }

        /// <summary>
        /// Initialises a new instance of an Entity declared in a DTD.
        /// </summary>
        /// <param name="name">The name of the entity.</param>
        /// <param name="literal">The literal value of the entity.</param>
        public Entity(string name, string literal, IEntityResolver resolver)
        {
            m_name = name;
            m_literal = literal;
            m_isInternal = true;
            m_resolver = resolver;
        }

        /// <summary>
        /// Initialises a new instance of an Entity declared in a DTD.
        /// </summary>
        /// <param name="name">The name of the entity.</param>
        /// <param name="baseUri">The baseUri for the entity to read from the TextReader.</param>
        /// <param name="stm">The TextReader to read the entity from.</param>
        public Entity(string name, Uri baseUri, TextReader stm, IEntityResolver resolver)
        {
            m_name = name;
            m_isInternal = true;
            m_stm = stm;
            m_resolvedUri = baseUri;
            m_isHtml = string.Equals(name, "html", StringComparison.OrdinalIgnoreCase);
            m_resolver = resolver;
        }

        /// <summary>
        /// The name of the entity.
        /// </summary>
        public string Name => m_name;

        /// <summary>
        /// True if the entity is the html element entity.
        /// </summary>
        public bool IsHtml
        {
            get => m_isHtml;
            set => m_isHtml = value;
        }

        /// <summary>
        /// The public identifier of this entity.
        /// </summary>
        public string PublicId => m_publicId;

        /// <summary>
        /// The Uri that is the source for this entity.
        /// </summary>
        public string Uri => m_uri;

        /// <summary>
        /// The resolved location of the DTD this entity is from.
        /// </summary>
        public Uri ResolvedUri
        {
            get
            {
                if (this.m_resolvedUri != null)
                    return this.m_resolvedUri;
                else if (m_parent != null)
                    return m_parent.ResolvedUri;
                else
                    return null;
            }
        }

        /// <summary>
        /// Gets the parent Entity of this Entity.
        /// </summary>
        public Entity Parent => m_parent;

        /// <summary>
        /// The last character read from the input stream for this entity.
        /// </summary>
        public char Lastchar => m_lastchar;

        /// <summary>
        /// The line on which this entity was defined.
        /// </summary>
        public int Line => m_line;

        /// <summary>
        /// The index into the line where this entity is defined.
        /// </summary>
        public int LinePosition
        {
            get
            {
                return this.m_absolutePos - this.m_lineStart + 1;
            }
        }

        /// <summary>
        /// Whether this entity is an internal entity or not.
        /// </summary>
        /// <value>true if this entity is internal, otherwise false.</value>
        public bool IsInternal
        {
            get
            {
                return m_isInternal;
            }
        }

        /// <summary>
        /// The literal value of this entity.
        /// </summary>
        public string Literal
        {
            get
            {
                return m_literal;
            }
        }

        /// <summary>
        /// The <see cref="LiteralType"/> of this entity.
        /// </summary>
        public LiteralType LiteralType
        {
            get
            {
                return m_literalType;
            }
        }

        /// <summary>
        /// Whether the last char read for this entity is a whitespace character.
        /// </summary>
        public bool IsWhitespace
        {
            get
            {
                return m_isWhitespace;
            }
        }
        
        /// <summary>
        /// Reads the next character from the DTD stream.
        /// </summary>
        /// <returns>The next character from the DTD stream.</returns>
        public char ReadChar()
        {
            char ch = (char)this.m_stm.Read();
            if (ch == 0)
            {
                // convert nulls to whitespace, since they are not valid in XML anyway.
                ch = ' ';
            }
            this.m_absolutePos++;
            if (ch == 0xa)
            {
                m_isWhitespace = true;
                this.m_lineStart = this.m_absolutePos + 1;
                this.m_line++;
            } 
            else if (ch == ' ' || ch == '\t')
            {
                m_isWhitespace = true;
                if (m_lastchar == 0xd)
                {
                    this.m_lineStart = this.m_absolutePos;
                    m_line++;
                }
            }
            else if (ch == 0xd)
            {
                m_isWhitespace = true;
            }
            else
            {
                m_isWhitespace = false;
                if (m_lastchar == 0xd)
                {
                    m_line++;
                    this.m_lineStart = this.m_absolutePos;
                }
            } 

            m_lastchar = ch;
            return ch;
        }

        /// <summary>
        /// Begins processing an entity.
        /// </summary>
        /// <param name="parent">The parent of this entity.</param>
        /// <param name="baseUri">The base Uri for processing this entity within.</param>
        public void Open(Entity parent, Uri baseUri)
        {
            this.m_parent = parent;
            if (parent != null)
                this.m_isHtml = parent.IsHtml;
            this.m_line = 1;
            if (m_isInternal)
            {
                if (this.m_literal != null)
                    this.m_stm = new StringReader(this.m_literal);
            } 
            else if (this.m_uri is null)
            {
                this.Error("Unresolvable entity '{0}'", this.m_name);
            }
            else
            {
                if (baseUri != null)
                {
                    this.m_resolvedUri = new Uri(baseUri, this.m_uri);
                }
                else
                {
                    this.m_resolvedUri = new Uri(this.m_uri, UriKind.RelativeOrAbsolute);
                }

                IEntityContent content = m_resolver.GetContent(this.ResolvedUri);
                Stream stream = content.Open();

                if (StringUtilities.EqualsIgnoreCase(content.MimeType, "text/html"))
                {
                    this.m_isHtml = true;
                }
                this.m_resolvedUri = content.Redirect;

                this.m_weOwnTheStream = true;
                HtmlStream html = new HtmlStream(stream, content.Encoding);
                this.m_encoding = html.Encoding;
                this.m_stm = html;
            }
        }

        /// <summary>
        /// Gets the character encoding for this entity.
        /// </summary>
        public Encoding Encoding
        {
            get
            {
                return this.m_encoding;
            }
        }
        
        /// <summary>
        /// Closes the reader from which the entity is being read.
        /// </summary>
        public void Close()
        {
            if (this.m_weOwnTheStream) 
                this.m_stm.Dispose();
        }

        /// <summary>
        /// Returns the next character after any whitespace.
        /// </summary>
        /// <returns>The next character that is not whitespace.</returns>
        public char SkipWhitespace()
        {
            char ch = m_lastchar;
            while (ch != Entity.EOF && (ch == ' ' || ch == '\r' || ch == '\n' || ch == '\t'))
            {
                ch = ReadChar();
            }
            return ch;
        }

        /// <summary>
        /// Scans a token from the input stream and returns the result.
        /// </summary>
        /// <param name="sb">The <see cref="StringBuilder"/> to use to process the token.</param>
        /// <param name="term">A set of characters to look for as terminators for the token.</param>
        /// <param name="nmtoken">true if the token should be a NMToken, otherwise false.</param>
        /// <returns>The scanned token.</returns>
        public string ScanToken(StringBuilder sb, string term, bool nmtoken)
        {
            if (sb is null)
                throw new ArgumentNullException(nameof(sb));

            if (term is null)
                throw new ArgumentNullException(nameof(term));

            sb.Length = 0;
            char ch = m_lastchar;
            if (nmtoken && ch != '_' && !char.IsLetter(ch))
            {
                throw new SgmlParseException($"Invalid name start character '{ch}'");
            }

            while (ch != Entity.EOF && term.IndexOf(ch) < 0)
            {
                if (!nmtoken || ch == '_' || ch == '.' || ch == '-' || ch == ':' || char.IsLetterOrDigit(ch)) {
                    sb.Append(ch);
                } 
                else {
                    throw new SgmlParseException($"Invalid name character '{ch}'");
                }
                ch = ReadChar();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Read a literal from the input stream.
        /// </summary>
        /// <param name="sb">The <see cref="StringBuilder"/> to use to build the literal.</param>
        /// <param name="quote">The delimiter for the literal.</param>
        /// <returns>The literal scanned from the input stream.</returns>
        public string ScanLiteral(StringBuilder sb, char quote)
        {
            if (sb is null)
                throw new ArgumentNullException(nameof(sb));

            sb.Length = 0;
            char ch = ReadChar();
            while (ch != Entity.EOF && ch != quote)
            {
                if (ch == '&')
                {
                    ch = ReadChar();
                    if (ch == '#')
                    {
                        string charent = ExpandCharEntity();
                        sb.Append(charent);
                        ch = this.m_lastchar;
                    } 
                    else
                    {
                        sb.Append('&');
                        sb.Append(ch);
                        ch = ReadChar();
                    }
                }               
                else
                {
                    sb.Append(ch);
                    ch = ReadChar();
                }
            }

            ReadChar(); // consume end quote.
            return sb.ToString();
        }

        /// <summary>
        /// Reads input until the end of the input stream or until a string of terminator characters is found.
        /// </summary>
        /// <param name="sb">The <see cref="StringBuilder"/> to use to build the string.</param>
        /// <param name="type">The type of the element being read (only used in reporting errors).</param>
        /// <param name="terminators">The string of terminator characters to look for.</param>
        /// <returns>The string read from the input stream.</returns>
        public string ScanToEnd(StringBuilder sb, string type, string terminators)
        {
            if (terminators is null)
                throw new ArgumentNullException(nameof(terminators));

            if (sb != null)
                sb.Length = 0;

            int start = m_line;
            // This method scans over a chunk of text looking for the
            // termination sequence specified by the 'terminators' parameter.
            char ch = ReadChar();            
            int state = 0;
            char next = terminators[state];
            while (ch != Entity.EOF)
            {
                if (ch == next)
                {
                    state++;
                    if (state >= terminators.Length)
                    {
                        // found it!
                        break;
                    }
                    next = terminators[state];
                }
                else if (state > 0)
                {
                    // char didn't match, so go back and see how much does still match.
                    int i = state - 1;
                    int newstate = 0;
                    while (i >= 0 && newstate == 0)
                    {
                        if (terminators[i] == ch)
                        {
                            // character is part of the terminators pattern, ok, so see if we can
                            // match all the way back to the beginning of the pattern.
                            int j = 1;
                            while (i - j >= 0)
                            {
                                if (terminators[i - j] != terminators[state - j])
                                    break;

                                j++;
                            }

                            if (j > i)
                            {
                                newstate = i + 1;
                            }
                        }
                        else
                        {
                            i--;
                        }
                    }

                    if (sb != null)
                    {
                        i = (i < 0) ? 1 : 0;
                        for (int k = 0; k <= state - newstate - i; k++)
                        {
                            sb.Append(terminators[k]); 
                        }

                        if (i > 0) // see if we've matched this char or not
                            sb.Append(ch); // if not then append it to buffer.
                    }

                    state = newstate;
                    next = terminators[newstate];
                }
                else
                {
                    if (sb != null)
                        sb.Append(ch);
                }

                ch = ReadChar();
            }

            if (ch == 0)
                Error(type + " starting on line {0} was never closed", start);

            ReadChar(); // consume last char in termination sequence.
            if (sb != null)
                return sb.ToString();
            else
                return string.Empty;
        }

        /// <summary>
        /// Expands a character entity to be read from the input stream.
        /// </summary>
        /// <returns>The string for the character entity.</returns>
        public string ExpandCharEntity()
        {
            int v = ReadNumericEntityCode(out string value);
            if(v == -1)
            {
                return value;
            }

            // HACK ALERT: IE and Netscape map the unicode characters 
            if (this.m_isHtml && v >= 0x80 & v <= 0x9F)
            {
                // This range of control characters is mapped to Windows-1252!
                int i = v - 0x80;
                int unicode = CtrlMap[i];
                return Convert.ToChar(unicode).ToString();
            }

            if (0xD800 <= v && v <= 0xDBFF)
            {
                // high surrogate
                if (m_lastchar == '&')
                {
                    char ch = ReadChar();
                    if (ch == '#')
                    {
                        int v2 = ReadNumericEntityCode(out string value2);
                        if(v2 == -1)
                        {
                            return value + ";" + value2;
                        }
                        if (0xDC00 <= v2 && v2 <= 0xDFFF)
                        {
                            // low surrogate
                            v = char.ConvertToUtf32((char)v, (char)v2);
                        }
                    }
                    else
                    {
                        Error("Premature {0} parsing surrogate pair", ch);
                    }
                }
                else
                {
                    Error("Premature {0} parsing surrogate pair", m_lastchar);
                }
            }

            // NOTE (steveb): we need to use ConvertFromUtf32 to allow for extended numeric encodings
            return char.ConvertFromUtf32(v);
        }

        private int ReadNumericEntityCode(out string value)
        {
            int v = 0;
            char ch = ReadChar();
            value = "&#";
            if (ch == 'x')
            {
                bool sawHexDigit = false;
                value += "x";
                ch = ReadChar();
                for (; ch != Entity.EOF && ch != ';'; ch = ReadChar())
                {
                    int p = 0;
                    if (ch >= '0' && ch <= '9')
                    {
                        p = (int)(ch - '0');
                        sawHexDigit = true;
                    } 
                    else if (ch >= 'a' && ch <= 'f')
                    {
                        p = (int)(ch - 'a') + 10;
                        sawHexDigit = true;
                    } 
                    else if (ch >= 'A' && ch <= 'F')
                    {
                        p = (int)(ch - 'A') + 10;
                        sawHexDigit = true;
                    }
                    else
                    {
                        break; //we must be done!
                        //Error("Hex digit out of range '{0}'", (int)ch);
                    }
                    value += ch;
                    v = (v*16) + p;
                }
                if (!sawHexDigit)
                {
                    return -1;
                }
            } 
            else
            {
                bool sawDigit = false;
                for (; ch != Entity.EOF && ch != ';'; ch = ReadChar())
                {
                    if (ch >= '0' && ch <= '9')
                    {
                        v = (v*10) + (int)(ch - '0');
                        sawDigit = true;
                    } 
                    else
                    {
                        break; // we must be done!
                        //Error("Decimal digit out of range '{0}'", (int)ch);
                    }
                    value += ch;
                }
                if (!sawDigit)
                {
                    return -1;
                }
            }
            if (ch == 0)
            {
                Error("Premature {0} parsing entity reference", ch);
            }
            else if (ch == ';')
            {
                ReadChar();
            }
            return v;
        }

        static readonly int[] CtrlMap = new int[] {
                                             // This is the windows-1252 mapping of the code points 0x80 through 0x9f.
                                             8364, 129, 8218, 402, 8222, 8230, 8224, 8225, 710, 8240, 352, 8249, 338, 141,
                                             381, 143, 144, 8216, 8217, 8220, 8221, 8226, 8211, 8212, 732, 8482, 353, 8250, 
                                             339, 157, 382, 376
                                         };

        /// <summary>
        /// Raise a processing error.
        /// </summary>
        /// <param name="msg">The error message to use in the exception.</param>
        /// <exception cref="SgmlParseException">Always thrown.</exception>
        public void Error(string msg)
        {
            throw new SgmlParseException(msg, this);
        }

        /// <summary>
        /// Raise a processing error.
        /// </summary>
        /// <param name="msg">The error message to use in the exception.</param>
        /// <param name="ch">The unexpected character causing the error.</param>
        /// <exception cref="SgmlParseException">Always thrown.</exception>
        public void Error(string msg, char ch)
        {
            string str = (ch == Entity.EOF) ? "EOF" : char.ToString(ch);
            throw new SgmlParseException(string.Format(CultureInfo.CurrentUICulture, msg, str), this);
        }

        /// <summary>
        /// Raise a processing error.
        /// </summary>
        /// <param name="msg">The error message to use in the exception.</param>
        /// <param name="x">The value causing the error.</param>
        /// <exception cref="SgmlParseException">Always thrown.</exception>
        public void Error(string msg, int x)
        {
            throw new SgmlParseException(string.Format(CultureInfo.CurrentUICulture, msg, x), this);
        }

        /// <summary>
        /// Raise a processing error.
        /// </summary>
        /// <param name="msg">The error message to use in the exception.</param>
        /// <param name="arg">The argument for the error.</param>
        /// <exception cref="SgmlParseException">Always thrown.</exception>
        public void Error(string msg, string arg)
        {
            throw new SgmlParseException(string.Format(CultureInfo.CurrentUICulture, msg, arg), this);
        }

        /// <summary>
        /// Returns a string giving information on how the entity is referenced and declared, walking up the parents until the top level parent entity is found.
        /// </summary>
        /// <returns>Contextual information for the entity.</returns>
        public string Context()
        {
            Entity p = this;
            StringBuilder sb = new StringBuilder();
            while (p != null)
            {
                string msg;
                if (p.m_isInternal)
                {
                    msg = string.Format(CultureInfo.InvariantCulture, "\nReferenced on line {0}, position {1} of internal entity '{2}'", p.m_line, p.LinePosition, p.m_name);
                } 
                else {
                    msg = string.Format(CultureInfo.InvariantCulture, "\nReferenced on line {0}, position {1} of '{2}' entity at [{3}]", p.m_line, p.LinePosition, p.m_name, p.ResolvedUri.AbsolutePath);
                }
                sb.Append(msg);
                p = p.Parent;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Checks whether a token denotes a literal entity or not.
        /// </summary>
        /// <param name="token">The token to check.</param>
        /// <returns>true if the token is "CDATA", "SDATA" or "PI", otherwise false.</returns>
        public static bool IsLiteralType(string token)
        {
            return string.Equals(token, "CDATA", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "SDATA", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "PI", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sets the entity to be a literal of the type specified.
        /// </summary>
        /// <param name="token">One of "CDATA", "SDATA" or "PI".</param>
        public void SetLiteralType(string token)
        {
            switch (token)
            {
                case "CDATA":
                    this.m_literalType = LiteralType.CDATA;
                    break;
                case "SDATA":
                    this.m_literalType = LiteralType.SDATA;
                    break;
                case "PI":
                    this.m_literalType = LiteralType.PI;
                    break;
            }
        }

#region IDisposable Members

        /// <summary>
        /// The finalizer for the Entity class.
        /// </summary>
        ~Entity()
        {
            Dispose(false);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources. 
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources. 
        /// </summary>
        /// <param name="isDisposing">true if this method has been called by user code, false if it has been called through a finalizer.</param>
        protected virtual void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                if (m_stm != null)
                {
                    m_stm.Dispose();
                    m_stm = null;
                }
            }
        }

#endregion
    }

    // This class decodes an HTML/XML stream correctly.
    internal sealed class HtmlStream : TextReader
    {
        private readonly Stream stm;
        private readonly byte[] rawBuffer;
        private int rawPos;
        private int rawUsed;
        private Encoding m_encoding;
        private readonly Decoder m_decoder;
        private char[] m_buffer;
        private int used;
        private int pos;
        private const int BUFSIZE = 16384;
        private const int EOF = -1;

        public HtmlStream(Stream stm, Encoding defaultEncoding)
        {            
            defaultEncoding ??= Encoding.UTF8; // default is UTF8

            if (!stm.CanSeek){
                // Need to be able to seek to sniff correctly.
                stm = CopyToMemoryStream(stm);
            }
            this.stm = stm;
            rawBuffer = new Byte[BUFSIZE];
            rawUsed = stm.Read(rawBuffer, 0, 4); // maximum byte order mark
            this.m_buffer = new char[BUFSIZE];

            // Check byte order marks
            this.m_decoder = AutoDetectEncoding(rawBuffer, ref rawPos, rawUsed);
            int bom = rawPos;
            if (this.m_decoder is null)
            {
                this.m_decoder = defaultEncoding.GetDecoder();
                rawUsed += stm.Read(rawBuffer, 4, BUFSIZE-4);                
                DecodeBlock();
                // Now sniff to see if there is an XML declaration or HTML <META> tag.
                Decoder sd = SniffEncoding();
                if (sd != null) {
                    this.m_decoder = sd;
                }
            }            

            // Reset to get ready for Read()
            this.stm.Seek(0, SeekOrigin.Begin);
            this.pos = this.used = 0;
            // skip bom
            if (bom>0){
                stm.Read(this.rawBuffer, 0, bom);
            }
            this.rawPos = this.rawUsed = 0;            
        }

        public Encoding Encoding => this.m_encoding;

        private static Stream CopyToMemoryStream(Stream s)
        {
            int size = 100000; // large heap is more efficient
            byte[] copyBuff = new byte[size];
            int len;
            MemoryStream r = new MemoryStream();
            while ((len = s.Read(copyBuff, 0, size)) > 0)
                r.Write(copyBuff, 0, len);

            r.Seek(0, SeekOrigin.Begin);                            
            s.Dispose();
            return r;
        }

        internal void DecodeBlock() {
            // shift current chars to beginning.
            if (pos > 0) {
                if (pos < used) {
                    System.Array.Copy(m_buffer, pos, m_buffer, 0, used - pos);
                }
                used -= pos;
                pos = 0;
            }
            int len = m_decoder.GetCharCount(rawBuffer, rawPos, rawUsed - rawPos);
            int available = m_buffer.Length - used;
            if (available < len) {
                char[] newbuf = new char[m_buffer.Length + len];
                System.Array.Copy(m_buffer, pos, newbuf, 0, used - pos);
                m_buffer = newbuf;
            }
            used = pos + m_decoder.GetChars(rawBuffer, rawPos, rawUsed - rawPos, m_buffer, pos);
            rawPos = rawUsed; // consumed the whole buffer!
        }
        internal static Decoder AutoDetectEncoding(byte[] buffer, ref int index, int length) {
            if (4 <= (length - index)) {
                uint w = (uint)buffer[index + 0] << 24 | (uint)buffer[index + 1] << 16 | (uint)buffer[index + 2] << 8 | (uint)buffer[index + 3];
                // see if it's a 4-byte encoding
                switch (w) {
                    case 0xfefffeff: 
                        index += 4; 
                        return new Ucs4DecoderBigEngian();

                    case 0xfffefffe: 
                        index += 4; 
                        return new Ucs4DecoderLittleEndian();

                    case 0x3c000000: 
                        goto case 0xfefffeff;

                    case 0x0000003c: 
                        goto case 0xfffefffe;
                }
                w >>= 8;
                if (w == 0xefbbbf) {
                    index += 3;
                    return Encoding.UTF8.GetDecoder();
                }
                w >>= 8;
                switch (w) {
                    case 0xfeff: 
                        index += 2; 
                        return UnicodeEncoding.BigEndianUnicode.GetDecoder();

                    case 0xfffe: 
                        index += 2; 
                        return new UnicodeEncoding(false, false).GetDecoder();

                    case 0x3c00: 
                        goto case 0xfeff;

                    case 0x003c: 
                        goto case 0xfffe;
                }
            }
            return null;
        }
        private int ReadChar() {
            // Read only up to end of current buffer then stop.
            if (pos < used) return m_buffer[pos++];
            return EOF;
        }
        private int PeekChar() {
            int ch = ReadChar();
            if (ch != EOF) {
                pos--;
            }
            return ch;
        }
        private bool SniffPattern(string pattern) {
            int ch = PeekChar();
            if (ch != pattern[0]) return false;
            for (int i = 0, n = pattern.Length; ch != EOF && i < n; i++) {
                ch = ReadChar();
                char m = pattern[i];
                if (ch != m) {
                    return false;
                }
            }
            return true;
        }
        private void SniffWhitespace() {
            char ch = (char)PeekChar();
            while (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') {
                int i = pos;
                ch = (char)ReadChar();
                if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n')
                    pos = i;
            }
        }

        private string SniffLiteral() {
            int quoteChar = PeekChar();
            if (quoteChar == '\'' || quoteChar == '"') {
                ReadChar();// consume quote char
                int i = this.pos;
                int ch = ReadChar();
                while (ch != EOF && ch != quoteChar) {
                    ch = ReadChar();
                }
                return (pos>i) ? new string(m_buffer, i, pos - i - 1) : "";
            }
            return null;
        }
        private string SniffAttribute(string name) {
            SniffWhitespace();
            string id = SniffName();
            if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase)) {
                SniffWhitespace();
                if (SniffPattern("=")) {
                    SniffWhitespace();
                    return SniffLiteral();
                }
            }
            return null;
        }
        private string SniffAttribute(out string name) {
            SniffWhitespace();
            name = SniffName();
            if (name != null){
                SniffWhitespace();
                if (SniffPattern("=")) {
                    SniffWhitespace();
                    return SniffLiteral();
                }
            }
            return null;
        }
        private void SniffTerminator(string term) {
            int ch = ReadChar();
            int i = 0;
            int n = term.Length;
            while (i < n && ch != EOF) {
                if (term[i] == ch) {
                    i++;
                    if (i == n) break;
                } else {
                    i = 0; // reset.
                }
                ch = ReadChar();
            }
        }

        internal Decoder SniffEncoding()
        {
            Decoder decoder = null;
            if (SniffPattern("<?xml"))
            {
                string version = SniffAttribute("version");
                if (version != null)
                {
                    string encoding = SniffAttribute("encoding");
                    if (encoding != null)
                    {
                        try
                        {
                            Encoding enc = Encoding.GetEncoding(encoding);
                            if (enc != null)
                            {
                                this.m_encoding = enc;
                                return enc.GetDecoder();
                            }
                        }
                        catch (ArgumentException)
                        {
                            // oh well then.
                        }
                    }
                    SniffTerminator(">");
                }
            } 
            if (decoder is null) {
                return SniffMeta();
            }
            return null;
        }

        internal Decoder SniffMeta()
        {
            int i = ReadChar();            
            while (i != EOF)
            {
                char ch = (char)i;
                if (ch == '<')
                {
                    string name = SniffName();
                    if (name != null && StringUtilities.EqualsIgnoreCase(name, "meta"))
                    {
                        string httpequiv = null;
                        string content = null;
                        while (true)
                        {
                            string value = SniffAttribute(out name);
                            if (name is null)
                                break;

                            if (StringUtilities.EqualsIgnoreCase(name, "http-equiv"))
                            {
                                httpequiv = value;
                            }
                            else if (StringUtilities.EqualsIgnoreCase(name, "content"))
                            {
                                content = value;
                            }
                        }

                        if (httpequiv != null && StringUtilities.EqualsIgnoreCase(httpequiv, "content-type") && content != null)
                        {
                            int j = content.IndexOf("charset");
                            if (j >= 0)
                            {
                                //charset=utf-8
                                j = content.IndexOf("=", j);
                                if (j >= 0)
                                {
                                    j++;
                                    int k = content.IndexOf(";", j);
                                    if (k<0) k = content.Length;
                                    string charset = content.Substring(j, k-j).Trim();
                                    try
                                    {
                                        Encoding e = Encoding.GetEncoding(charset);
                                        this.m_encoding = e;
                                        return e.GetDecoder();
                                    } catch (ArgumentException) {}
                                }                                
                            }
                        }
                    }
                }
                i = ReadChar();

            }
            return null;
        }

        internal string SniffName()
        {
            int c = PeekChar();
            if (c == EOF)
                return null;
            char ch = (char)c;
            int start = pos;
            while (pos < used - 1 && (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ':'))
                ch = m_buffer[++pos];

            if (start == pos)
                return null;

            return new string(m_buffer, start, pos - start);
        }

        [SuppressMessage("Microsoft.Performance", "CA1811", Justification = "Kept for potential future usage.")]
        internal void SkipWhitespace()
        {
            char ch = (char)PeekChar();
            while (pos < used - 1 && (ch == ' ' || ch == '\r' || ch == '\n'))
                ch = m_buffer[++pos];
        }

        [SuppressMessage("Microsoft.Performance", "CA1811", Justification = "Kept for potential future usage.")]
        internal void SkipTo(char what)
        {
            char ch = (char)PeekChar();
            while (pos < used - 1 && (ch != what))
                ch = m_buffer[++pos];
        }

        [SuppressMessage("Microsoft.Performance", "CA1811", Justification = "Kept for potential future usage.")]
        internal string ParseAttribute()
        {
            SkipTo('=');
            if (pos < used)
            {
                pos++;
                SkipWhitespace();
                if (pos < used) {
                    char quote = m_buffer[pos];
                    pos++;
                    int start = pos;
                    SkipTo(quote);
                    if (pos < used) {
                        string result = new string(m_buffer, start, pos - start);
                        pos++;
                        return result;
                    }
                }
            }
            return null;
        }
        public override int Peek() {
            int result = Read();
            if (result != EOF) {
                pos--;
            }
            return result;
        }
        public override int Read()
        {
            if (pos == used)
            {
                rawUsed = stm.Read(rawBuffer, 0, rawBuffer.Length);
                rawPos = 0;
                if (rawUsed == 0) return EOF;
                DecodeBlock();
            }
            if (pos < used) return m_buffer[pos++];
            return -1;
        }

        public override int Read(char[] buffer, int start, int length) {
            if (pos == used) {
                rawUsed = stm.Read(rawBuffer, 0, rawBuffer.Length);
                rawPos = 0;
                if (rawUsed == 0) return -1;
                DecodeBlock();
            }
            if (pos < used) {
                length = Math.Min(used - pos, length);
                Array.Copy(this.m_buffer, pos, buffer, start, length);
                pos += length;
                return length;
            }
            return 0;
        }

        public override int ReadBlock(char[] data, int index, int count)
        {
            return Read(data, index, count);
        }

        // Read up to end of line, or full buffer, whichever comes first.
        [SuppressMessage("Microsoft.Performance", "CA1811", Justification = "Kept for potential future usage.")]
        public int ReadLine(char[] buffer, int start, int length)
        {
            int i = 0;
            int ch = ReadChar();
            while (ch != EOF) {
                buffer[i+start] = (char)ch;
                i++;
                if (i+start == length) 
                    break; // buffer is full

                if (ch == '\r' ) {
                    if (PeekChar() == '\n') {
                        ch = ReadChar();
                        buffer[i + start] = (char)ch;
                        i++;
                    }
                    break;
                } else if (ch == '\n') {
                    break;
                }
                ch = ReadChar();
            }
            return i;
        }

        public override string ReadToEnd() {
            char[] buffer = new char[100000]; // large block heap is more efficient
            int len = 0;
            StringBuilder sb = new StringBuilder();
            while ((len = Read(buffer, 0, buffer.Length)) > 0) {
                sb.Append(buffer, 0, len);
            }
            return sb.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            stm.Dispose();
        }
    }

    internal abstract class Ucs4Decoder : Decoder {
        internal byte[] temp = new byte[4];
        internal int tempBytes = 0;
        public override int GetCharCount(byte[] bytes, int index, int count) {
            return (count + tempBytes) / 4;
        }
        internal abstract int GetFullChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) {
            int i = tempBytes;

            if (tempBytes > 0) {
                for (; i < 4; i++) {
                    temp[i] = bytes[byteIndex];
                    byteIndex++;
                    byteCount--;
                }
                i = 1;
                GetFullChars(temp, 0, 4, chars, charIndex);
                charIndex++;
            } else
                i = 0;
            i = GetFullChars(bytes, byteIndex, byteCount, chars, charIndex) + i;

            int j = (tempBytes + byteCount) % 4;
            byteCount += byteIndex;
            byteIndex = byteCount - j;
            tempBytes = 0;

            if (byteIndex >= 0)
                for (; byteIndex < byteCount; byteIndex++) {
                    temp[tempBytes] = bytes[byteIndex];
                    tempBytes++;
                }
            return i;
        }
        internal static char UnicodeToUTF16(UInt32 code) {
            byte lowerByte, higherByte;
            lowerByte = (byte)(0xD7C0 + (code >> 10));
            higherByte = (byte)(0xDC00 | code & 0x3ff);
            return ((char)((higherByte << 8) | lowerByte));
        }
    }

    internal class Ucs4DecoderBigEngian : Ucs4Decoder {
        internal override int GetFullChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) {
            UInt32 code;
            int i, j;
            byteCount += byteIndex;
            for (i = byteIndex, j = charIndex; i + 3 < byteCount; ) {
                code = (UInt32)(((bytes[i + 3]) << 24) | (bytes[i + 2] << 16) | (bytes[i + 1] << 8) | (bytes[i]));
                if (code > 0x10FFFF) {
                    throw new SgmlParseException($"Invalid character 0x{code:x} in encoding");
                } else if (code > 0xFFFF) {
                    chars[j] = UnicodeToUTF16(code);
                    j++;
                } else {
                    if (code >= 0xD800 && code <= 0xDFFF) {
                        throw new SgmlParseException($"Invalid character 0x{code:x} in encoding");
                    } else {
                        chars[j] = (char)code;
                    }
                }
                j++;
                i += 4;
            }
            return j - charIndex;
        }
    }

    internal class Ucs4DecoderLittleEndian : Ucs4Decoder {
        internal override int GetFullChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) {
            UInt32 code;
            int i, j;
            byteCount += byteIndex;
            for (i = byteIndex, j = charIndex; i + 3 < byteCount; ) {
                code = (UInt32)(((bytes[i]) << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | (bytes[i + 3]));
                if (code > 0x10FFFF) {
                    throw new SgmlParseException($"Invalid character 0x{code:x} in encoding");
                } else if (code > 0xFFFF) {
                    chars[j] = UnicodeToUTF16(code);
                    j++;
                } else {
                    if (code >= 0xD800 && code <= 0xDFFF) {
                        throw new SgmlParseException($"Invalid character 0x{code:x} in encoding");
                    } else {
                        chars[j] = (char)code;
                    }
                }
                j++;
                i += 4;
            }
            return j - charIndex;
        }
    }

    /// <summary>
    /// An element declaration in a DTD.
    /// </summary>
    public class ElementDecl
    {
        private readonly string m_name;
        private readonly bool m_startTagOptional;
        private readonly bool m_endTagOptional;
        private readonly ContentModel m_contentModel;
        private readonly string[] m_inclusions;
        private readonly string[] m_exclusions;
        private Dictionary<string, AttDef> m_attList;

        /// <summary>
        /// Initialises a new element declaration instance.
        /// </summary>
        /// <param name="name">The name of the element.</param>
        /// <param name="sto">Whether the start tag is optional.</param>
        /// <param name="eto">Whether the end tag is optional.</param>
        /// <param name="cm">The <see cref="ContentModel"/> of the element.</param>
        /// <param name="inclusions"></param>
        /// <param name="exclusions"></param>
        public ElementDecl(string name, bool sto, bool eto, ContentModel cm, string[] inclusions, string[] exclusions)
        {
            m_name = name;
            m_startTagOptional = sto;
            m_endTagOptional = eto;
            m_contentModel = cm;
            m_inclusions = inclusions;
            m_exclusions = exclusions;
        }

        /// <summary>
        /// The element name.
        /// </summary>
        public string Name => m_name;

        /// <summary>
        /// The <see cref="Sgml.ContentModel"/> of the element declaration.
        /// </summary>
        public ContentModel ContentModel => m_contentModel;

        /// <summary>
        /// Whether the end tag of the element is optional.
        /// </summary>
        /// <value>true if the end tag of the element is optional, otherwise false.</value>
        public bool EndTagOptional => m_endTagOptional;

        /// <summary>
        /// Whether the start tag of the element is optional.
        /// </summary>
        /// <value>true if the start tag of the element is optional, otherwise false.</value>
        public bool StartTagOptional => m_startTagOptional;

        /// <summary>
        /// Finds the attribute definition with the specified name.
        /// </summary>
        /// <param name="name">The name of the <see cref="AttDef"/> to find.</param>
        /// <returns>The <see cref="AttDef"/> with the specified name.</returns>
        /// <exception cref="InvalidOperationException">If the attribute list has not yet been initialised.</exception>
        public AttDef FindAttribute(string name)
        {
            if (m_attList is null)
                throw new InvalidOperationException("The attribute list for the element declaration has not been initialised.");

            m_attList.TryGetValue(name.ToUpperInvariant(), out AttDef a);
            return a;
        }

        /// <summary>
        /// Adds attribute definitions to the element declaration.
        /// </summary>
        /// <param name="list">The list of attribute definitions to add.</param>
        public void AddAttDefs(Dictionary<string, AttDef> list)
        {
            if (list is null)
                throw new ArgumentNullException(nameof(list));

            if (m_attList is null) 
            {
                m_attList = list;
            } 
            else 
            {
                foreach (AttDef a in list.Values) 
                {
                    if (!m_attList.ContainsKey(a.Name)) 
                    {
                        m_attList.Add(a.Name, a);
                    }
                }
            }
        }

        /// <summary>
        /// Tests whether this element can contain another specified element.
        /// </summary>
        /// <param name="name">The name of the element to check for.</param>
        /// <param name="dtd">The DTD to use to do the check.</param>
        /// <returns>True if the specified element can be contained by this element.</returns>
        public bool CanContain(string name, SgmlDtd dtd)
        {            
            // return true if this element is allowed to contain the given element.
            if (m_exclusions != null) 
            {
                foreach (string s in m_exclusions) 
                {
                    if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            if (m_inclusions != null) 
            {
                foreach (string s in m_inclusions) 
                {
                    if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return m_contentModel.CanContain(name, dtd);
        }
    }

    /// <summary>
    /// Where nested subelements cannot occur within an element, its contents can be declared to consist of one of the types of declared content contained in this enumeration.
    /// </summary>
    public enum DeclaredContent
    {
        /// <summary>
        /// Not defined.
        /// </summary>
        Default,
        
        /// <summary>
        /// Character data (CDATA), which contains only valid SGML characters.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        CDATA,
        
        /// <summary>
        /// Replaceable character data (RCDATA), which can contain text, character references and/or general entity references that resolve to character data.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        RCDATA,
        
        /// <summary>
        /// Empty element (EMPTY), i.e. having no contents, or contents that can be generated by the program.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        EMPTY
    }

    /// <summary>
    /// Defines the content model for an element.
    /// </summary>
    public class ContentModel
    {
        private DeclaredContent m_declaredContent;
        private int m_currentDepth;
        private Group m_model;

        /// <summary>
        /// Initialises a new instance of the <see cref="ContentModel"/> class.
        /// </summary>
        public ContentModel()
        {
            m_model = new Group(null);
        }

        /// <summary>
        /// The number of groups on the stack.
        /// </summary>
        public int CurrentDepth => m_currentDepth;

        /// <summary>
        /// The allowed child content, specifying if nested children are not allowed and if so, what content is allowed.
        /// </summary>
        public DeclaredContent DeclaredContent => m_declaredContent;

        /// <summary>
        /// Begins processing of a nested model group.
        /// </summary>
        public void PushGroup()
        {
            m_model = new Group(m_model);
            m_currentDepth++;
        }

        /// <summary>
        /// Finishes processing of a nested model group.
        /// </summary>
        /// <returns>The current depth of the group nesting, or -1 if there are no more groups to pop.</returns>
        public int PopGroup()
        {
            if (m_currentDepth == 0)
                return -1;

            m_currentDepth--;
            m_model.Parent.AddGroup(m_model);
            m_model = m_model.Parent;
            return m_currentDepth;
        }

        /// <summary>
        /// Adds a new symbol to the current group's members.
        /// </summary>
        /// <param name="sym">The symbol to add.</param>
        public void AddSymbol(string sym)
        {
            m_model.AddSymbol(sym);
        }

        /// <summary>
        /// Adds a connector onto the member list for the current group.
        /// </summary>
        /// <param name="c">The connector character to add.</param>
        /// <exception cref="SgmlParseException">
        /// If the content is not mixed and has no members yet, or if the group type has been set and the
        /// connector does not match the group type.
        /// </exception>
        public void AddConnector(char c)
        {
            m_model.AddConnector(c);
        }

        /// <summary>
        /// Adds an occurrence character for the current model group, setting it's <see cref="Occurrence"/> value.
        /// </summary>
        /// <param name="c">The occurrence character.</param>
        public void AddOccurrence(char c)
        {
            m_model.AddOccurrence(c);
        }

        /// <summary>
        /// Sets the contained content for the content model.
        /// </summary>
        /// <param name="dc">The text specified the permissible declared child content.</param>
        public void SetDeclaredContent(string dc)
        {
            // TODO: Validate that this can never combine with nexted groups?
            this.m_declaredContent = dc switch
            {
                "EMPTY" => DeclaredContent.EMPTY,
                "RCDATA" => DeclaredContent.RCDATA,
                "CDATA" => DeclaredContent.CDATA,
                _ => throw new SgmlParseException($"Declared content type '{dc}' is not supported")
            };
        }

        /// <summary>
        /// Checks whether an element using this group can contain a specified element.
        /// </summary>
        /// <param name="name">The name of the element to look for.</param>
        /// <param name="dtd">The DTD to use during the checking.</param>
        /// <returns>true if an element using this group can contain the element, otherwise false.</returns>
        public bool CanContain(string name, SgmlDtd dtd)
        {
            if (m_declaredContent != DeclaredContent.Default)
                return false; // empty or text only node.

            return m_model.CanContain(name, dtd);
        }
    }

    /// <summary>
    /// The type of the content model group, defining the order in which child elements can occur.
    /// </summary>
    public enum GroupType
    {
        /// <summary>
        /// No model group.
        /// </summary>
        None,
        
        /// <summary>
        /// All elements must occur, in any order.
        /// </summary>
        And,
        
        /// <summary>
        /// One (and only one) must occur.
        /// </summary>
        Or,
        
        /// <summary>
        /// All element must occur, in the specified order.
        /// </summary>
        Sequence 
    };

    /// <summary>
    /// Qualifies the occurrence of a child element within a content model group.
    /// </summary>
    public enum Occurrence
    {
        /// <summary>
        /// The element is required and must occur only once.
        /// </summary>
        Required,
        
        /// <summary>
        /// The element is optional and must occur once at most.
        /// </summary>
        Optional,
        
        /// <summary>
        /// The element is optional and can be repeated.
        /// </summary>
        ZeroOrMore,
        
        /// <summary>
        /// The element must occur at least once or more times.
        /// </summary>
        OneOrMore
    }

    /// <summary>
    /// Defines a group of elements nested within another element.
    /// </summary>
    public class Group
    {
        private readonly Group m_parent;
        private readonly List<object> Members;
        private GroupType m_groupType;
        private Occurrence m_occurrence;
        private bool Mixed;

        /// <summary>
        /// The <see cref="Occurrence"/> of this group.
        /// </summary>
        public Occurrence Occurrence => m_occurrence;

        /// <summary>
        /// Checks whether the group contains only text.
        /// </summary>
        /// <value>true if the group is of mixed content and has no members, otherwise false.</value>
        public bool TextOnly => this.Mixed && Members.Count == 0;

        /// <summary>
        /// The parent group of this group.
        /// </summary>
        public Group Parent => m_parent;
        /// <summary>
        /// Initialises a new Content Model Group.
        /// </summary>
        /// <param name="parent">The parent model group.</param>
        public Group(Group parent)
        {
            m_parent = parent;
            Members = new List<object>();
            m_groupType = GroupType.None;
            m_occurrence = Occurrence.Required;
        }

        /// <summary>
        /// Adds a new child model group to the end of the group's members.
        /// </summary>
        /// <param name="g">The model group to add.</param>
        public void AddGroup(Group g)
        {
            Members.Add(g);
        }

        /// <summary>
        /// Adds a new symbol to the group's members.
        /// </summary>
        /// <param name="sym">The symbol to add.</param>
        public void AddSymbol(string sym)
        {
            if (string.Equals(sym, "#PCDATA", StringComparison.OrdinalIgnoreCase)) 
            {               
                Mixed = true;
            } 
            else 
            {
                Members.Add(sym);
            }
        }

        /// <summary>
        /// Adds a connector onto the member list.
        /// </summary>
        /// <param name="c">The connector character to add.</param>
        /// <exception cref="SgmlParseException">
        /// If the content is not mixed and has no members yet, or if the group type has been set and the
        /// connector does not match the group type.
        /// </exception>
        public void AddConnector(char c)
        {
            if (!Mixed && Members.Count == 0) 
            {
                throw new SgmlParseException($"Missing token before connector '{c}'.");
            }

            GroupType gt = GroupType.None;
            switch (c) 
            {
                case ',': 
                    gt = GroupType.Sequence;
                    break;
                case '|':
                    gt = GroupType.Or;
                    break;
                case '&':
                    gt = GroupType.And;
                    break;
            }

            if (this.m_groupType != GroupType.None && this.m_groupType != gt) 
            {
                throw new SgmlParseException($"Connector '{c}' is inconsistent with {m_groupType} group.");
            }

            m_groupType = gt;
        }

        /// <summary>
        /// Adds an occurrence character for this group, setting it's <see cref="Occurrence"/> value.
        /// </summary>
        /// <param name="c">The occurrence character.</param>
        public void AddOccurrence(char c)
        {
            Occurrence o = Occurrence.Required;
            switch (c) 
            {
                case '?': 
                    o = Occurrence.Optional;
                    break;
                case '+':
                    o = Occurrence.OneOrMore;
                    break;
                case '*':
                    o = Occurrence.ZeroOrMore;
                    break;
            }

            m_occurrence = o;
        }

        /// <summary>
        /// Checks whether an element using this group can contain a specified element.
        /// </summary>
        /// <param name="name">The name of the element to look for.</param>
        /// <param name="dtd">The DTD to use during the checking.</param>
        /// <returns>true if an element using this group can contain the element, otherwise false.</returns>
        /// <remarks>
        /// Rough approximation - this is really assuming an "Or" group
        /// </remarks>
        public bool CanContain(string name, SgmlDtd dtd)
        {
            if (dtd is null)
                throw new ArgumentNullException(nameof(dtd));

            // Do a simple search of members.
            foreach (object obj in Members) 
            {
                if (obj is string s) 
                {
                    if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                } 
            }
            // didn't find it, so do a more expensive search over child elements
            // that have optional start tags and over child groups.
            foreach (object obj in Members) 
            {
                if (obj as string is string s)
                {
                    ElementDecl e = dtd.FindElement(s);
                    if (e != null) 
                    {
                        if (e.StartTagOptional) 
                        {
                            // tricky case, the start tag is optional so element may be
                            // allowed inside this guy!
                            if (e.CanContain(name, dtd))
                                return true;
                        }
                    }
                } 
                else 
                {
                    Group m = (Group)obj;
                    if (m.CanContain(name, dtd)) 
                        return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Defines the different possible attribute types.
    /// </summary>
    public enum AttributeType
    {
        /// <summary>
        /// Attribute type not specified.
        /// </summary>
        Default,

        /// <summary>
        /// The attribute contains text (with no markup).
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        CDATA,
        
        /// <summary>
        /// The attribute contains an entity declared in a DTD.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        ENTITY,

        /// <summary>
        /// The attribute contains a number of entities declared in a DTD.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        ENTITIES,
        
        /// <summary>
        /// The attribute is an id attribute uniquely identifie the element it appears on.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        [SuppressMessage("Microsoft.Naming", "CA1706", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        ID,
        
        /// <summary>
        /// The attribute value can be any declared subdocument or data entity name.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        IDREF,
        
        /// <summary>
        /// The attribute value is a list of (space separated) declared subdocument or data entity names.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        IDREFS,
        
        /// <summary>
        /// The attribute value is a SGML Name.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        NAME,
        
        /// <summary>
        /// The attribute value is a list of (space separated) SGML Names.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        NAMES,
        
        /// <summary>
        /// The attribute value is an XML name token (i.e. contains only name characters, but in this case with digits and other valid name characters accepted as the first character).
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        NMTOKEN,

        /// <summary>
        /// The attribute value is a list of (space separated) XML NMTokens.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        NMTOKENS,

        /// <summary>
        /// The attribute value is a number.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        NUMBER,
        
        /// <summary>
        /// The attribute value is a list of (space separated) numbers.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        NUMBERS,
        
        /// <summary>
        /// The attribute value is a number token (i.e. a name that starts with a number).
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        NUTOKEN,
        
        /// <summary>
        /// The attribute value is a list of number tokens.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        NUTOKENS,
        
        /// <summary>
        /// Attribute value is a member of the bracketed list of notation names that qualifies this reserved name.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        NOTATION,
        
        /// <summary>
        /// The attribute value is one of a set of allowed names.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1705", Justification = "This capitalisation is appropriate since the value it represents has all upper-case capitalisation.")]
        ENUMERATION
    }

    /// <summary>
    /// Defines the different constraints on an attribute's presence on an element.
    /// </summary>
    public enum AttributePresence
    {
        /// <summary>
        /// The attribute has a default value, and its presence is optional.
        /// </summary>
        Default,

        /// <summary>
        /// The attribute has a fixed value, if present.
        /// </summary>
        Fixed,

        /// <summary>
        /// The attribute must always be present on every element.
        /// </summary>
        Required,
        
        /// <summary>
        /// The element is optional.
        /// </summary>
        Implied
    }

    /// <summary>
    /// An attribute definition in a DTD.
    /// </summary>
    public class AttDef
    {
        private readonly string m_name;
        private AttributeType m_type;
        private string[] m_enumValues;
        private string m_default;
        private AttributePresence m_presence;

        /// <summary>
        /// Initialises a new instance of the <see cref="AttDef"/> class.
        /// </summary>
        /// <param name="name">The name of the attribute.</param>
        public AttDef(string name)
        {
            m_name = name;
        }

        /// <summary>
        /// The name of the attribute declared by this attribute definition.
        /// </summary>
        public string Name => m_name;

        /// <summary>
        /// Gets of sets the default value of the attribute.
        /// </summary>
        public string Default
        {
            get => m_default;
            set => m_default = value;
        }

        /// <summary>
        /// The constraints on the attribute's presence on an element.
        /// </summary>
        public AttributePresence AttributePresence => m_presence;

        /// <summary>
        /// Gets or sets the possible enumerated values for the attribute.
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1819", Justification = "Changing this would break backwards compatibility with previous code using this library.")]
        public string[] EnumValues => m_enumValues;

        /// <summary>
        /// Sets the attribute definition to have an enumerated value.
        /// </summary>
        /// <param name="enumValues">The possible values in the enumeration.</param>
        /// <param name="type">The type to set the attribute to.</param>
        /// <exception cref="ArgumentException">If the type parameter is not either <see cref="AttributeType.ENUMERATION"/> or <see cref="AttributeType.NOTATION"/>.</exception>
        public void SetEnumeratedType(string[] enumValues, AttributeType type)
        {
            if (type != AttributeType.ENUMERATION && type != AttributeType.NOTATION)
                throw new ArgumentException($"AttributeType {type} is not valid for an attribute definition with an enumerated value.");

            m_enumValues = enumValues;
            m_type = type;
        }

        /// <summary>
        /// The <see cref="AttributeType"/> of the attribute declaration.
        /// </summary>
        public AttributeType Type => m_type;

        /// <summary>
        /// Sets the type of the attribute definition.
        /// </summary>
        /// <param name="type">The string representation of the attribute type, corresponding to the values in the <see cref="AttributeType"/> enumeration.</param>
        public void SetType(string type)
        {
            m_type = type switch
            {
                "CDATA" => AttributeType.CDATA,
                "ENTITY" => AttributeType.ENTITY,
                "ENTITIES" => AttributeType.ENTITIES,
                "ID" => AttributeType.ID,
                "IDREF" => AttributeType.IDREF,
                "IDREFS" => AttributeType.IDREFS,
                "NAME" => AttributeType.NAME,
                "NAMES" => AttributeType.NAMES,
                "NMTOKEN" => AttributeType.NMTOKEN,
                "NMTOKENS" => AttributeType.NMTOKENS,
                "NUMBER" => AttributeType.NUMBER,
                "NUMBERS" => AttributeType.NUMBERS,
                "NUTOKEN" => AttributeType.NUTOKEN,
                "NUTOKENS" => AttributeType.NUTOKENS,
                _ => throw new SgmlParseException($"Attribute type '{type}' is not supported")
            };
        }

        /// <summary>
        /// Sets the attribute presence declaration.
        /// </summary>
        /// <param name="token">The string representation of the attribute presence, corresponding to one of the values in the <see cref="AttributePresence"/> enumeration.</param>
        /// <returns>true if the attribute presence implies the element has a default value.</returns>
        public bool SetPresence(string token)
        {
            bool hasDefault = true;
            if (string.Equals(token, "FIXED", StringComparison.OrdinalIgnoreCase)) 
            {
                m_presence = AttributePresence.Fixed;             
            } 
            else if (string.Equals(token, "REQUIRED", StringComparison.OrdinalIgnoreCase)) 
            {
                m_presence = AttributePresence.Required;
                hasDefault = false;
            }
            else if (string.Equals(token, "IMPLIED", StringComparison.OrdinalIgnoreCase)) 
            {
                m_presence = AttributePresence.Implied;
                hasDefault = false;
            }
            else 
            {
                throw new SgmlParseException($"Attribute value '{token}' not supported");
            }

            return hasDefault;
        }
    }

/* JB: Replaced this with a Dictionary<string, AttDef>
    public class AttList : IEnumerable
    {
        Hashtable AttDefs;
        
        public AttList()
        {
            AttDefs = new Hashtable();
        }

        public void Add(AttDef a)
        {
            AttDefs.Add(a.Name, a);
        }

        public AttDef this[string name] => (AttDef)AttDefs[name];

        public IEnumerator GetEnumerator()
        {
            return AttDefs.Values.GetEnumerator();
        }
    }
*/
    /// <summary>
    /// Provides DTD parsing and support for the SgmlParser framework.
    /// </summary>
    public class SgmlDtd
    {
        private string m_name;

        private readonly Dictionary<string, ElementDecl> m_elements;
        private readonly Dictionary<string, Entity> m_pentities;
        private readonly Dictionary<string, Entity> m_entities;
        private readonly StringBuilder m_sb;
        private Entity m_current;
        private readonly IEntityResolver m_resolver;

        /// <summary>
        /// Initialises a new instance of the <see cref="SgmlDtd"/> class.
        /// </summary>
        /// <param name="name">The name of the DTD.</param>
        /// <param name="nt">The <see cref="XmlNameTable"/> is NOT used.</param>
        public SgmlDtd(string name, XmlNameTable nt, IEntityResolver resolver)
        {
            this.m_name = name;
            this.m_elements = new Dictionary<string,ElementDecl>();
            this.m_pentities = new Dictionary<string, Entity>();
            this.m_entities = new Dictionary<string, Entity>();
            this.m_sb = new StringBuilder();
            this.m_resolver = resolver;
        }

        /// <summary>
        /// The name of the DTD.
        /// </summary>
        public string Name
        {
            get => m_name;
            set => m_name = value;
        }

        /// <summary>
        /// Gets the XmlNameTable associated with this implementation.
        /// </summary>
        /// <value>The XmlNameTable enabling you to get the atomized version of a string within the node.</value>
        public XmlNameTable NameTable => null;

        /// <summary>
        /// Parses a DTD and creates a <see cref="SgmlDtd"/> instance that encapsulates the DTD.
        /// </summary>
        /// <param name="baseUri">The base URI of the DTD.</param>
        /// <param name="name">The name of the DTD.</param>
        /// <param name="pubid"></param>
        /// <param name="url"></param>
        /// <param name="subset"></param>
        /// <param name="nt">The <see cref="XmlNameTable"/> is NOT used.</param>
        /// <returns>A new <see cref="SgmlDtd"/> instance that encapsulates the DTD.</returns>
        public static SgmlDtd Parse(Uri baseUri, string name, string pubid, string url, string subset, XmlNameTable nt, IEntityResolver resolver)
        {
            SgmlDtd dtd = new SgmlDtd(name, nt, resolver);
            if (!string.IsNullOrEmpty(url))
            {
                dtd.PushEntity(baseUri, new Entity(dtd.Name, pubid, url, resolver));
            }

            if (!string.IsNullOrEmpty(subset))
            {
                dtd.PushEntity(baseUri, new Entity(name, subset, resolver));
            }

            try 
            {
                dtd.Parse();
            } 
            catch (Exception e)
            {
                throw new SgmlParseException(e.Message + dtd.m_current.Context());
            }

            return dtd;
        }

        /// <summary>
        /// Parses a DTD and creates a <see cref="SgmlDtd"/> instance that encapsulates the DTD.
        /// </summary>
        /// <param name="baseUri">The base URI of the DTD.</param>
        /// <param name="name">The name of the DTD.</param>
        /// <param name="input">The reader to load the DTD from.</param>
        /// <param name="subset"></param>
        /// <param name="nt">The <see cref="XmlNameTable"/> is NOT used.</param>
        /// <returns>A new <see cref="SgmlDtd"/> instance that encapsulates the DTD.</returns>
        [SuppressMessage("Microsoft.Reliability", "CA2000", Justification = "The entities created here are not temporary and should not be disposed here.")]
        public static SgmlDtd Parse(Uri baseUri, string name, TextReader input, string subset, XmlNameTable nt, IEntityResolver resolver)
        {
            SgmlDtd dtd = new SgmlDtd(name, nt, resolver);
            dtd.PushEntity(baseUri, new Entity(dtd.Name, baseUri, input, resolver));
            if (!string.IsNullOrEmpty(subset))
            {
                dtd.PushEntity(baseUri, new Entity(name, subset, resolver));
            }

            try
            {
                dtd.Parse();
            } 
            catch (Exception e)
            {
                throw new SgmlParseException(e.Message + dtd.m_current.Context());
            }

            return dtd;
        }

        /// <summary>
        /// Finds an entity in the DTD with the specified name.
        /// </summary>
        /// <param name="name">The name of the <see cref="Entity"/> to find.</param>
        /// <returns>The specified Entity from the DTD.</returns>
        public Entity FindEntity(string name)
        {
            this.m_entities.TryGetValue(name, out Entity e);
            return e;
        }

        /// <summary>
        /// Finds an element declaration in the DTD with the specified name.
        /// </summary>
        /// <param name="name">The name of the <see cref="ElementDecl"/> to find and return.</param>
        /// <returns>The <see cref="ElementDecl"/> matching the specified name.</returns>
        public ElementDecl FindElement(string name)
        {
            m_elements.TryGetValue(name.ToUpperInvariant(), out ElementDecl el);
            return el;
        }

        //-------------------------------- Parser -------------------------
        private void PushEntity(Uri baseUri, Entity e)
        {
            e.Open(this.m_current, baseUri);
            this.m_current = e;
            this.m_current.ReadChar();
        }

        private void PopEntity()
        {
            if (this.m_current != null) this.m_current.Close();
            if (this.m_current.Parent != null) 
            {
                this.m_current = this.m_current.Parent;
            } 
            else 
            {
                this.m_current = null;
            }
        }

        private void Parse()
        {
            char ch = this.m_current.Lastchar;
            while (true) 
            {
                switch (ch) 
                {
                    case Entity.EOF:
                        PopEntity();
                        if (this.m_current is null)
                            return;
                        ch = this.m_current.Lastchar;
                        break;
                    case ' ':
                    case '\n':
                    case '\r':
                    case '\t':
                        ch = this.m_current.ReadChar();
                        break;
                    case '<':
                        ParseMarkup();
                        ch = this.m_current.ReadChar();
                        break;
                    case '%':
                        Entity e = ParseParameterEntity(SgmlDtd.WhiteSpace);
                        try 
                        {
                            PushEntity(this.m_current.ResolvedUri, e);
                        } 
                        catch (Exception ex) 
                        {
                            // BUG: need an error log.
                            Debug.WriteLine(ex.Message + this.m_current.Context());
                        }
                        ch = this.m_current.Lastchar;
                        break;
                    default:
                        this.m_current.Error("Unexpected character '{0}'", ch);
                        break;
                }               
            }
        }

        void ParseMarkup()
        {
            char ch = this.m_current.ReadChar();
            if (ch != '!') 
            {
                this.m_current.Error("Found '{0}', but expecing declaration starting with '<!'");
                return;
            }
            ch = this.m_current.ReadChar();
            if (ch == '-') 
            {
                ch = this.m_current.ReadChar();
                if (ch != '-') this.m_current.Error("Expecting comment '<!--' but found {0}", ch);
                this.m_current.ScanToEnd(this.m_sb, "Comment", "-->");
            } 
            else if (ch == '[') 
            {
                ParseMarkedSection();
            }
            else 
            {
                string token = this.m_current.ScanToken(this.m_sb, SgmlDtd.WhiteSpace, true);
                switch (token) 
                {
                    case "ENTITY":
                        ParseEntity();
                        break;
                    case "ELEMENT":
                        ParseElementDecl();
                        break;
                    case "ATTLIST":
                        ParseAttList();
                        break;
                    default:
                        this.m_current.Error("Invalid declaration '<!{0}'.  Expecting 'ENTITY', 'ELEMENT' or 'ATTLIST'.", token);
                        break;
                }
            }
        }

        char ParseDeclComments()
        {
            char ch = this.m_current.Lastchar;
            while (ch == '-') 
            {
                ch = ParseDeclComment(true);
            }
            return ch;
        }

        char ParseDeclComment(bool full)
        {
            // This method scans over a comment inside a markup declaration.
            char ch = this.m_current.ReadChar();
            if (full && ch != '-') this.m_current.Error("Expecting comment delimiter '--' but found {0}", ch);
            this.m_current.ScanToEnd(this.m_sb, "Markup Comment", "--");
            return this.m_current.SkipWhitespace();
        }

        void ParseMarkedSection()
        {
            // <![^ name [ ... ]]>
            this.m_current.ReadChar(); // move to next char.
            string name = ScanName("[");
            if (string.Equals(name, "INCLUDE", StringComparison.OrdinalIgnoreCase)) 
            {
                ParseIncludeSection();
            } 
            else if (string.Equals(name, "IGNORE", StringComparison.OrdinalIgnoreCase)) 
            {
                ParseIgnoreSection();
            }
            else 
            {
                this.m_current.Error("Unsupported marked section type '{0}'", name);
            }
        }

        [SuppressMessage("Microsoft.Performance", "CA1822", Justification = "This is not yet implemented and will use 'this' in the future.")]
        [SuppressMessage("Microsoft.Globalization", "CA1303", Justification = "The use of a literal here is only due to this not yet being implemented.")]
        private void ParseIncludeSection()
        {
            throw new NotImplementedException("Include Section");
        }

        void ParseIgnoreSection()
        {
            char ch = this.m_current.SkipWhitespace();
            if (ch != '[') this.m_current.Error("Expecting '[' but found {0}", ch);
            this.m_current.ScanToEnd(this.m_sb, "Conditional Section", "]]>");
        }

        string ScanName(string term)
        {
            // skip whitespace, scan name (which may be parameter entity reference
            // which is then expanded to a name)
            char ch = this.m_current.SkipWhitespace();
            if (ch == '%') 
            {
                Entity e = ParseParameterEntity(term);
                ch = this.m_current.Lastchar;
                // bugbug - need to support external and nested parameter entities
                if (!e.IsInternal) throw new NotSupportedException("External parameter entity resolution");
                return e.Literal.Trim();
            } 
            else 
            {
                return this.m_current.ScanToken(this.m_sb, term, true);
            }
        }

        private Entity ParseParameterEntity(string term)
        {
            // almost the same as this.current.ScanToken, except we also terminate on ';'
            this.m_current.ReadChar();
            string name =  this.m_current.ScanToken(this.m_sb, ";"+term, false);
            if (this.m_current.Lastchar == ';') 
                this.m_current.ReadChar();
            Entity e = GetParameterEntity(name);
            return e;
        }

        private Entity GetParameterEntity(string name)
        {
            m_pentities.TryGetValue(name, out Entity e);
            if (e is null)
                this.m_current.Error("Reference to undefined parameter entity '{0}'", name);

            return e;
        }

        /// <summary>
        /// Returns a dictionary for looking up entities by their <see cref="Entity.Literal"/> value.
        /// </summary>
        /// <returns>A dictionary for looking up entities by their <see cref="Entity.Literal"/> value.</returns>
        [SuppressMessage("Microsoft.Design", "CA1024", Justification = "This method creates and copies a dictionary, so exposing it as a property is not appropriate.")]
        public Dictionary<string, Entity> GetEntitiesLiteralNameLookup()
        {
            Dictionary<string, Entity> hashtable = new Dictionary<string, Entity>();
            foreach (Entity entity in this.m_entities.Values)
                hashtable[entity.Literal] = entity;

            return hashtable;
        }
        
        private const string WhiteSpace = " \r\n\t";

        private void ParseEntity()
        {
            char ch = this.m_current.SkipWhitespace();
            bool pe = (ch == '%');
            if (pe)
            {
                // parameter entity.
                this.m_current.ReadChar(); // move to next char
                ch = this.m_current.SkipWhitespace();
            }
            string name = this.m_current.ScanToken(this.m_sb, SgmlDtd.WhiteSpace, true);
            ch = this.m_current.SkipWhitespace();
            Entity e = null;
            if (ch == '"' || ch == '\'') 
            {
                string literal = this.m_current.ScanLiteral(this.m_sb, ch);
                e = new Entity(name, literal, m_resolver);                
            } 
            else 
            {
                string pubid = null;
                string extid = null;
                string tok = this.m_current.ScanToken(this.m_sb, SgmlDtd.WhiteSpace, true);
                if (Entity.IsLiteralType(tok))
                {
                    ch = this.m_current.SkipWhitespace();
                    string literal = this.m_current.ScanLiteral(this.m_sb, ch);
                    e = new Entity(name, literal, m_resolver);
                    e.SetLiteralType(tok);
                }
                else 
                {
                    extid = tok;
                    if (string.Equals(extid, "PUBLIC", StringComparison.OrdinalIgnoreCase)) 
                    {
                        ch = this.m_current.SkipWhitespace();
                        if (ch == '"' || ch == '\'') 
                        {
                            pubid = this.m_current.ScanLiteral(this.m_sb, ch);
                        } 
                        else 
                        {
                            this.m_current.Error("Expecting public identifier literal but found '{0}'",ch);
                        }
                    } 
                    else if (!string.Equals(extid, "SYSTEM", StringComparison.OrdinalIgnoreCase)) 
                    {
                        this.m_current.Error("Invalid external identifier '{0}'.  Expecing 'PUBLIC' or 'SYSTEM'.", extid);
                    }
                    string uri = null;
                    ch = this.m_current.SkipWhitespace();
                    if (ch == '"' || ch == '\'') 
                    {
                        uri = this.m_current.ScanLiteral(this.m_sb, ch);
                    } 
                    else if (ch != '>')
                    {
                        this.m_current.Error("Expecting system identifier literal but found '{0}'",ch);
                    }
                    e = new Entity(name, pubid, uri, m_resolver);
                }
            }
            ch = this.m_current.SkipWhitespace();
            if (ch == '-') 
                ch = ParseDeclComments();
            if (ch != '>') 
            {
                this.m_current.Error("Expecting end of entity declaration '>' but found '{0}'", ch);  
            }           
            if (pe)
                this.m_pentities.Add(e.Name, e);
            else
                this.m_entities.Add(e.Name, e);
        }

        private void ParseElementDecl()
        {
            char ch = this.m_current.SkipWhitespace();
            string[] names = ParseNameGroup(ch, true);
            ch = char.ToUpperInvariant(this.m_current.SkipWhitespace());
            bool sto = false;
            bool eto = false;
            if (ch == 'O' || ch == '-') {
                sto = (ch == 'O'); // start tag optional?   
                this.m_current.ReadChar();
                ch = char.ToUpperInvariant(this.m_current.SkipWhitespace());
                if (ch == 'O' || ch == '-'){
                    eto = (ch == 'O'); // end tag optional? 
                    ch = this.m_current.ReadChar();
                }
            }
            ch = this.m_current.SkipWhitespace();
            ContentModel cm = ParseContentModel(ch);
            ch = this.m_current.SkipWhitespace();

            string [] exclusions = null;
            string [] inclusions = null;

            if (ch == '-') 
            {
                ch = this.m_current.ReadChar();
                if (ch == '(') 
                {
                    exclusions = ParseNameGroup(ch, true);
                    ch = this.m_current.SkipWhitespace();
                }
                else if (ch == '-') 
                {
                    ch = ParseDeclComment(false);
                } 
                else 
                {
                    this.m_current.Error("Invalid syntax at '{0}'", ch);  
                }
            }

            if (ch == '-') 
                ch = ParseDeclComments();

            if (ch == '+') 
            {
                ch = this.m_current.ReadChar();
                if (ch != '(') 
                {
                    this.m_current.Error("Expecting inclusions name group", ch);  
                }
                inclusions = ParseNameGroup(ch, true);
                ch = this.m_current.SkipWhitespace();
            }

            if (ch == '-') 
                ch = ParseDeclComments();


            if (ch != '>') 
            {
                this.m_current.Error("Expecting end of ELEMENT declaration '>' but found '{0}'", ch); 
            }

            foreach (string name in names) 
            {
                string atom = name.ToUpperInvariant();
                this.m_elements.Add(atom, new ElementDecl(atom, sto, eto, cm, inclusions, exclusions));
            }
        }

        const string ngterm = " \r\n\t|,)";
        string[] ParseNameGroup(char ch, bool nmtokens)
        {
            List<string> names = new List<string>();
            if (ch == '(') 
            {
                ch = this.m_current.ReadChar();
                ch = this.m_current.SkipWhitespace();
                while (ch != ')') 
                {
                    // skip whitespace, scan name (which may be parameter entity reference
                    // which is then expanded to a name)                    
                    ch = this.m_current.SkipWhitespace();
                    if (ch == '%') 
                    {
                        Entity e = ParseParameterEntity(SgmlDtd.ngterm);
                        PushEntity(this.m_current.ResolvedUri, e);
                        ParseNameList(names, nmtokens);
                        PopEntity();
                        ch = this.m_current.Lastchar;
                    }
                    else 
                    {
                        string token = this.m_current.ScanToken(this.m_sb, SgmlDtd.ngterm, nmtokens);
                        token = token.ToUpperInvariant();
                        names.Add(token);
                    }
                    ch = this.m_current.SkipWhitespace();
                    if (ch == '|' || ch == ',') ch = this.m_current.ReadChar();
                }
                this.m_current.ReadChar(); // consume ')'
            } 
            else 
            {
                string name = this.m_current.ScanToken(this.m_sb, SgmlDtd.WhiteSpace, nmtokens);
                name = name.ToUpperInvariant();
                names.Add(name);
            }
            return (string[])names.ToArray();
        }

        void ParseNameList(List<string> names, bool nmtokens)
        {
            char ch = this.m_current.Lastchar;
            ch = this.m_current.SkipWhitespace();
            while (ch != Entity.EOF) 
            {
                string name;
                if (ch == '%') 
                {
                    Entity e = ParseParameterEntity(SgmlDtd.ngterm);
                    PushEntity(this.m_current.ResolvedUri, e);
                    ParseNameList(names, nmtokens);
                    PopEntity();
                    ch = this.m_current.Lastchar;
                } 
                else 
                {
                    name = this.m_current.ScanToken(this.m_sb, SgmlDtd.ngterm, true);
                    name = name.ToUpperInvariant();
                    names.Add(name);
                }
                ch = this.m_current.SkipWhitespace();
                if (ch == '|') 
                {
                    ch = this.m_current.ReadChar();
                    ch = this.m_current.SkipWhitespace();
                }
            }
        }

        const string dcterm = " \r\n\t>";
        private ContentModel ParseContentModel(char ch)
        {
            ContentModel cm = new ContentModel();
            if (ch == '(') 
            {
                this.m_current.ReadChar();
                ParseModel(')', cm);
                ch = this.m_current.ReadChar();
                if (ch == '?' || ch == '+' || ch == '*') 
                {
                    cm.AddOccurrence(ch);
                    this.m_current.ReadChar();
                }
            } 
            else if (ch == '%') 
            {
                Entity e = ParseParameterEntity(SgmlDtd.dcterm);
                PushEntity(this.m_current.ResolvedUri, e);
                cm = ParseContentModel(this.m_current.Lastchar);
                PopEntity(); // bugbug should be at EOF.
            }
            else
            {
                string dc = ScanName(SgmlDtd.dcterm);
                cm.SetDeclaredContent(dc);
            }
            return cm;
        }

        const string cmterm = " \r\n\t,&|()?+*";
        void ParseModel(char cmt, ContentModel cm)
        {
            // Called when part of the model is made up of the contents of a parameter entity
            int depth = cm.CurrentDepth;
            char ch = this.m_current.Lastchar;
            ch = this.m_current.SkipWhitespace();
            while (ch != cmt || cm.CurrentDepth > depth) // the entity must terminate while inside the content model.
            {
                if (ch == Entity.EOF) 
                {
                    this.m_current.Error("Content Model was not closed");
                }
                if (ch == '%') 
                {
                    Entity e = ParseParameterEntity(SgmlDtd.cmterm);
                    PushEntity(this.m_current.ResolvedUri, e);
                    ParseModel(Entity.EOF, cm);
                    PopEntity();                    
                    ch = this.m_current.SkipWhitespace();
                } 
                else if (ch == '(') 
                {
                    cm.PushGroup();
                    this.m_current.ReadChar();// consume '('
                    ch = this.m_current.SkipWhitespace();
                }
                else if (ch == ')') 
                {
                    ch = this.m_current.ReadChar();// consume ')'
                    if (ch == '*' || ch == '+' || ch == '?') 
                    {
                        cm.AddOccurrence(ch);
                        ch = this.m_current.ReadChar();
                    }
                    if (cm.PopGroup() < depth)
                    {
                        this.m_current.Error("Parameter entity cannot close a paren outside it's own scope");
                    }
                    ch = this.m_current.SkipWhitespace();
                }
                else if (ch == ',' || ch == '|' || ch == '&') 
                {
                    cm.AddConnector(ch);
                    this.m_current.ReadChar(); // skip connector
                    ch = this.m_current.SkipWhitespace();
                }
                else
                {
                    string token;
                    if (ch == '#') 
                    {
                        ch = this.m_current.ReadChar();
                        token = "#" + this.m_current.ScanToken(this.m_sb, SgmlDtd.cmterm, true); // since '#' is not a valid name character.
                    } 
                    else 
                    {
                        token = this.m_current.ScanToken(this.m_sb, SgmlDtd.cmterm, true);
                    }

                    token = token.ToUpperInvariant();
                    ch = this.m_current.Lastchar;
                    if (ch == '?' || ch == '+' || ch == '*') 
                    {
                        cm.PushGroup();
                        cm.AddSymbol(token);
                        cm.AddOccurrence(ch);
                        cm.PopGroup();
                        this.m_current.ReadChar(); // skip connector
                        ch = this.m_current.SkipWhitespace();
                    } 
                    else 
                    {
                        cm.AddSymbol(token);
                        ch = this.m_current.SkipWhitespace();
                    }                   
                }
            }
        }

        void ParseAttList()
        {
            char ch = this.m_current.SkipWhitespace();
            string[] names = ParseNameGroup(ch, true);          
            Dictionary<string, AttDef> attlist = new Dictionary<string, AttDef>();
            ParseAttList(attlist, '>');
            foreach (string name in names)
            {
                if (!m_elements.TryGetValue(name, out ElementDecl e)) 
                {
                    this.m_current.Error("ATTLIST references undefined ELEMENT {0}", name);
                }

                e.AddAttDefs(attlist);
            }
        }

        const string peterm = " \t\r\n>";
        void ParseAttList(Dictionary<string, AttDef> list, char term)
        {
            char ch = this.m_current.SkipWhitespace();
            while (ch != term) 
            {
                if (ch == '%') 
                {
                    Entity e = ParseParameterEntity(SgmlDtd.peterm);
                    PushEntity(this.m_current.ResolvedUri, e);
                    ParseAttList(list, Entity.EOF);
                    PopEntity();                    
                    ch = this.m_current.SkipWhitespace();
                } 
                else if (ch == '-') 
                {
                    ch = ParseDeclComments();
                }
                else
                {
                    AttDef a = ParseAttDef(ch);
                    list.Add(a.Name, a);
                }
                ch = this.m_current.SkipWhitespace();
            }
        }

        AttDef ParseAttDef(char ch)
        {
            ch = this.m_current.SkipWhitespace();
            string name = ScanName(SgmlDtd.WhiteSpace);
            name = name.ToUpperInvariant();
            AttDef attdef = new AttDef(name);

            ch = this.m_current.SkipWhitespace();
            if (ch == '-') 
                ch = ParseDeclComments();               

            ParseAttType(ch, attdef);

            ch = this.m_current.SkipWhitespace();
            if (ch == '-') 
                ch = ParseDeclComments();               

            ParseAttDefault(ch, attdef);

            ch = this.m_current.SkipWhitespace();
            if (ch == '-') 
                ch = ParseDeclComments();               

            return attdef;

        }

        void ParseAttType(char ch, AttDef attdef)
        {
            if (ch == '%')
            {
                Entity e = ParseParameterEntity(SgmlDtd.WhiteSpace);
                PushEntity(this.m_current.ResolvedUri, e);
                ParseAttType(this.m_current.Lastchar, attdef);
                PopEntity(); // bugbug - are we at the end of the entity?
                ch = this.m_current.Lastchar;
                return;
            }

            if (ch == '(') 
            {
                //attdef.EnumValues = ParseNameGroup(ch, false);  
                //attdef.Type = AttributeType.ENUMERATION;
                attdef.SetEnumeratedType(ParseNameGroup(ch, false), AttributeType.ENUMERATION);
            } 
            else 
            {
                string token = ScanName(SgmlDtd.WhiteSpace);
                if (string.Equals(token, "NOTATION", StringComparison.OrdinalIgnoreCase)) 
                {
                    ch = this.m_current.SkipWhitespace();
                    if (ch != '(') 
                    {
                        this.m_current.Error("Expecting name group '(', but found '{0}'", ch);
                    }
                    //attdef.Type = AttributeType.NOTATION;
                    //attdef.EnumValues = ParseNameGroup(ch, true);
                    attdef.SetEnumeratedType(ParseNameGroup(ch, true), AttributeType.NOTATION);
                } 
                else 
                {
                    attdef.SetType(token);
                }
            }
        }

        void ParseAttDefault(char ch, AttDef attdef)
        {
            if (ch == '%')
            {
                Entity e = ParseParameterEntity(SgmlDtd.WhiteSpace);
                PushEntity(this.m_current.ResolvedUri, e);
                ParseAttDefault(this.m_current.Lastchar, attdef);
                PopEntity(); // bugbug - are we at the end of the entity?
                ch = this.m_current.Lastchar;
                return;
            }

            bool hasdef = true;
            if (ch == '#') 
            {
                this.m_current.ReadChar();
                string token = this.m_current.ScanToken(this.m_sb, SgmlDtd.WhiteSpace, true);
                hasdef = attdef.SetPresence(token);
                ch = this.m_current.SkipWhitespace();
            } 
            if (hasdef) 
            {
                if (ch == '\'' || ch == '"') 
                {
                    string lit = this.m_current.ScanLiteral(this.m_sb, ch);
                    attdef.Default = lit;
                    ch = this.m_current.SkipWhitespace();
                }
                else
                {
                    string name = this.m_current.ScanToken(this.m_sb, SgmlDtd.WhiteSpace, false);
                    name = name.ToUpperInvariant();
                    attdef.Default = name; // bugbug - must be one of the enumerated names.
                    ch = this.m_current.SkipWhitespace();
                }
            }
        }
    }

    internal static class StringUtilities
    {
        public static bool EqualsIgnoreCase(string a, string b){
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
