// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Microsoft.AspNetCore.Razor.Language.CodeGeneration
{
    public sealed class CodeWriter
    {
        private const string InstanceMethodFormat = "{0}.{1}";

        private static readonly char[] CStyleStringLiteralEscapeChars = new char[]
        {
            '\r',
            '\t',
            '\"',
            '\'',
            '\\',
            '\0',
            '\n',
            '\u2028',
            '\u2029',
        };

        private static readonly char[] NewLineCharacters = { '\r', '\n' };

        private int _absoluteIndex;
        private int _currentLineIndex;
        private int _currentLineCharacterIndex;

        private StringBuilder Builder { get; } = new StringBuilder();

        public int CurrentIndent { get; set; }

        public bool IsAfterNewLine { get; private set; }

        public string NewLine { get; set; } = Environment.NewLine;

        public SourceLocation Location => new SourceLocation(_absoluteIndex, _currentLineIndex, _currentLineCharacterIndex);

        // Internal for testing
        internal CodeWriter Indent(int size)
        {
            if (IsAfterNewLine)
            {
                Builder.Append(' ', size);

                _currentLineCharacterIndex += size;
                _absoluteIndex += size;

                IsAfterNewLine = false;
            }

            return this;
        }

        public CodeWriter Write(string data)
        {
            if (data == null)
            {
                return this;
            }

            return Write(data, 0, data.Length);
        }

        public CodeWriter Write(string data, int index, int count)
        {
            if (data == null || count == 0)
            {
                return this;
            }

            Indent(CurrentIndent);

            Builder.Append(data, index, count);
            
            IsAfterNewLine = false;

            _absoluteIndex += count;

            // The data string might contain a partial newline where the previously
            // written string has part of the newline.
            var i = index;
            int? trailingPartStart = null;

            if (
                // Check the last character of the previous write operation.
                Builder.Length - count - 1 >= 0 &&
                Builder[Builder.Length - count - 1] == '\r' &&

                // Check the first character of the current write operation.
                Builder[Builder.Length - count] == '\n')
            {
                // This is newline that's spread across two writes. Skip the first character of the
                // current write operation.
                //
                // We don't need to increment our newline counter because we already did that when we
                // saw the \r.
                i += 1;
                trailingPartStart = 1;
            }

            // Iterate the string, stopping at each occurrence of a newline character. This lets us count the
            // newline occurrences and keep the index of the last one.
            while ((i = data.IndexOfAny(NewLineCharacters, i)) >= 0)
            {
                // Newline found.
                _currentLineIndex++;
                _currentLineCharacterIndex = 0;

                i++;

                // We might have stopped at a \r, so check if it's followed by \n and then advance the index to
                // start the next search after it.
                if (count > i &&
                    data[i - 1] == '\r' &&
                    data[i] == '\n')
                {
                    i++;
                }

                // The 'suffix' of the current line starts after this newline token.
                trailingPartStart = i;
            }

            if (trailingPartStart == null)
            {
                // No newlines, just add the length of the data buffer
                _currentLineCharacterIndex += count;
            }
            else
            {
                // Newlines found, add the trailing part of 'data'
                _currentLineCharacterIndex += (count - trailingPartStart.Value);
            }

            return this;
        }

        public CodeWriter WriteLine()
        {
            Builder.Append(NewLine);

            _currentLineIndex++;
            _currentLineCharacterIndex = 0;
            _absoluteIndex += NewLine.Length;

            IsAfterNewLine = true;

            return this;
        }

        public CodeWriter WriteLine(string data)
        {
            return Write(data).WriteLine();
        }

        public CodeWriter WritePadding(int offset, SourceSpan? span, CodeRenderingContext context)
        {
            if (span == null)
            {
                return this;
            }

            var basePadding = CalculatePadding();
            var resolvedPadding = Math.Max(basePadding - offset, 0);

            if (context.Options.IndentWithTabs)
            {
                // Avoid writing directly to the StringBuilder here, that will throw off the manual indexing 
                // done by the base class.
                var tabs = resolvedPadding / context.Options.IndentSize;
                for (var i = 0; i < tabs; i++)
                {
                    Write("\t");
                }

                var spaces = resolvedPadding % context.Options.IndentSize;
                for (var i = 0; i < spaces; i++)
                {
                    Write(" ");
                }
            }
            else
            {
                for (var i = 0; i < resolvedPadding; i++)
                {
                    Write(" ");
                }
            }

            return this;

            int CalculatePadding()
            {
                var spaceCount = 0;
                for (var i = span.Value.AbsoluteIndex - 1; i >= 0; i--)
                {
                    var @char = context.SourceDocument[i];
                    if (@char == '\n' || @char == '\r')
                    {
                        break;
                    }
                    else if (@char == '\t')
                    {
                        spaceCount += context.Options.IndentSize;
                    }
                    else
                    {
                        spaceCount++;
                    }
                }

                return spaceCount;
            }
        }

        public string GenerateCode()
        {
            return Builder.ToString();
        }

        public CodeWriter WriteStringLiteralExpression(string literal)
        {
            if (literal == null)
            {
                throw new ArgumentNullException(nameof(literal));
            }

            if (literal.Length >= 256 && literal.Length <= 1500 && literal.IndexOf('\0') == -1)
            {
                WriteVerbatimStringLiteral(literal);
            }
            else
            {
                WriteCStyleStringLiteral(literal);
            }

            return this;
        }

        private void WriteVerbatimStringLiteral(string literal)
        {
            Write("@\"");

            // We need to find the index of each '"' (double-quote) to escape it.
            var start = 0;
            int end;
            while ((end = literal.IndexOf('\"', start)) > -1)
            {
                Write(literal, start, end - start);

                Write("\"\"");

                start = end + 1;
            }

            Debug.Assert(end == -1); // We've hit all of the double-quotes.

            // Write the remainder after the last double-quote.
            Write(literal, start, literal.Length - start);

            Write("\"");
        }

        private void WriteCStyleStringLiteral(string literal)
        {
            // From CSharpCodeGenerator.QuoteSnippetStringCStyle in CodeDOM
            Write("\"");

            // We need to find the index of each escapable character to escape it.
            var start = 0;
            int end;
            while ((end = literal.IndexOfAny(CStyleStringLiteralEscapeChars, start)) > -1)
            {
                Write(literal, start, end - start);

                switch (literal[end])
                {
                    case '\r':
                        Write("\\r");
                        break;
                    case '\t':
                        Write("\\t");
                        break;
                    case '\"':
                        Write("\\\"");
                        break;
                    case '\'':
                        Write("\\\'");
                        break;
                    case '\\':
                        Write("\\\\");
                        break;
                    case '\0':
                        Write("\\\0");
                        break;
                    case '\n':
                        Write("\\n");
                        break;
                    case '\u2028':
                    case '\u2029':
                        Write("\\u");
                        Write(((int)literal[end]).ToString("X4", CultureInfo.InvariantCulture));
                        break;
                    default:
                        Debug.Assert(false, "Unknown escape character.");
                        break;
                }

                start = end + 1;
            }

            Debug.Assert(end == -1); // We've hit all of chars that need escaping.

            // Write the remainder after the last escaped char.
            Write(literal, start, literal.Length - start);

            Write("\"");
        }

        public CodeWriter WriteUsingDirective(string namespaceName)
        {
            if (namespaceName == null)
            {
                throw new ArgumentNullException(nameof(namespaceName));
            }

            Write("using ");
            Write(namespaceName);
            WriteLine(";");

            return this;
        }

        public CodeWriter WriteMethodCallExpressionStatement(string methodName)
        {
            if (methodName == null)
            {
                throw new ArgumentNullException(nameof(methodName));
            }

            return WriteMethodCallExpressionStatement(methodName, (IList<string>)Array.Empty<string>());
        }

        public CodeWriter WriteMethodCallExpressionStatement(string methodName, params string[] arguments)
        {
            if (methodName == null)
            {
                throw new ArgumentNullException(nameof(methodName));
            }

            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            return WriteMethodCallExpressionStatement(methodName, (IList<string>)arguments);
        }

        public CodeWriter WriteMethodCallExpressionStatement(string methodName, IList<string> arguments)
        {
            if (methodName == null)
            {
                throw new ArgumentNullException(nameof(methodName));
            }

            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            WriteStartMethodCall(methodName);

            for (var i = 0; i < arguments.Count; i++)
            {
                Write(arguments[i]);
                if (i < arguments.Count - 1)
                {
                    Write(", ");
                }
            }

            WriteEndMethodCall(endLine: false);
            WriteLine(";");

            return this;
        }

        public CodeWriter WriteFieldDeclaration(IList<string> modifiers, string typeName, string fieldName)
        {
            if (modifiers == null)
            {
                throw new ArgumentNullException(nameof(modifiers));
            }

            if (typeName == null)
            {
                throw new ArgumentNullException(nameof(typeName));
            }

            if (fieldName == null)
            {
                throw new ArgumentNullException(nameof(fieldName));
            }

            return WriteFieldDeclaration(modifiers, typeName, fieldName, null);
        }

        public CodeWriter WriteFieldDeclaration(IList<string> modifiers, string typeName, string fieldName, string value)
        {
            if (modifiers == null)
            {
                throw new ArgumentNullException(nameof(modifiers));
            }

            if (typeName == null)
            {
                throw new ArgumentNullException(nameof(typeName));
            }

            if (fieldName == null)
            {
                throw new ArgumentNullException(nameof(fieldName));
            }

            for (var i = 0; i < modifiers.Count; i++)
            {
                Write(modifiers[i]);
                Write(" ");
            }

            Write(typeName);
            Write(" ");
            Write(fieldName);

            if (value != null)
            {
                Write(" = ");
                Write(value);
            }

            Write(";");
            WriteLine();

            return this;
        }

        public CodeWriter WriteAutoPropertyDeclaration(IList<string> modifiers, string typeName, string propertyName)
        {
            if (modifiers == null)
            {
                throw new ArgumentNullException(nameof(modifiers));
            }

            if (typeName == null)
            {
                throw new ArgumentNullException(nameof(typeName));
            }

            if (propertyName == null)
            {
                throw new ArgumentNullException(nameof(propertyName));
            }

            for (var i = 0; i < modifiers.Count; i++)
            {
                Write(modifiers[i]);
                Write(" ");
            }

            Write(typeName);
            Write(" ");
            Write(propertyName);
            Write(" { get; set; }");
            WriteLine();

            return this;
        }

        public CSharpBlockScope BuildScope()
        {
            return new CSharpBlockScope(this);
        }

        public CSharpBlockScope BuildLambdaExpression(params string[] parameterNames)
        {
            if (parameterNames == null)
            {
                throw new ArgumentNullException(nameof(parameterNames));
            }

            return BuildLambdaExpression(async: false, parameterNames: parameterNames);
        }

        public CSharpBlockScope BuildAsyncLambdaExpression(params string[] parameterNames)
        {
            if (parameterNames == null)
            {
                throw new ArgumentNullException(nameof(parameterNames));
            }

            return BuildLambdaExpression(async: true, parameterNames: parameterNames);
        }

        private CSharpBlockScope BuildLambdaExpression(bool async, string[] parameterNames)
        {
            if (parameterNames == null)
            {
                throw new ArgumentNullException(nameof(parameterNames));
            }

            if (async)
            {
                Write("async");
            }

            Write("(").Write(string.Join(", ", parameterNames)).Write(") => ");

            var scope = new CSharpBlockScope(this);

            return scope;
        }

        public CSharpBlockScope BuildNamespaceDeclaration(string namespaceName)
        {
            if (namespaceName == null)
            {
                throw new ArgumentNullException(nameof(namespaceName));
            }

            Write("namespace ").WriteLine(namespaceName);

            return new CSharpBlockScope(this);
        }

        public CSharpBlockScope BuildClassDeclaration(IList<string> modifiers, string className)
        {
            if (modifiers == null)
            {
                throw new ArgumentNullException(nameof(modifiers));
            }

            if (className == null)
            {
                throw new ArgumentNullException(nameof(className));
            }

            return BuildClassDeclaration(modifiers, className, Array.Empty<string>());
        }

        public CSharpBlockScope BuildClassDeclaration(
            IList<string> modifiers,
            string className,
            IList<string> baseTypes)
        {
            if (modifiers == null)
            {
                throw new ArgumentNullException(nameof(modifiers));
            }

            if (className == null)
            {
                throw new ArgumentNullException(nameof(className));
            }

            if (baseTypes == null)
            {
                throw new ArgumentNullException(nameof(baseTypes));
            }

            for (var i = 0; i < modifiers.Count; i++)
            {
                Write(modifiers[i]);
                Write(" ");
            }

            Write("class ");
            Write(className);

            if (baseTypes.Count > 0)
            {
                Write(" : ");
                Write(baseTypes[0]);

                for (var i = 1; i < baseTypes.Count; i++)
                {
                    Write(", ");
                    Write(baseTypes[i]);
                }
            }

            WriteLine();

            return new CSharpBlockScope(this);
        }

        public CSharpBlockScope BuildMethodDeclaration(IList<string> modifiers, string returnType, string methodName)
        {
            if (modifiers == null)
            {
                throw new ArgumentNullException(nameof(modifiers));
            }

            if (returnType == null)
            {
                throw new ArgumentNullException(nameof(returnType));
            }

            if (methodName == null)
            {
                throw new ArgumentNullException(nameof(methodName));
            }

            return BuildMethodDeclaration(modifiers, returnType, methodName, Array.Empty<(string, string)>());
        }

        public CSharpBlockScope BuildMethodDeclaration(
            IList<string> modifiers,
            string returnType,
            string methodName,
            IList<(string typeName, string parameterName)> parameters)
        {
            if (modifiers == null)
            {
                throw new ArgumentNullException(nameof(modifiers));
            }

            if (returnType == null)
            {
                throw new ArgumentNullException(nameof(returnType));
            }

            if (methodName == null)
            {
                throw new ArgumentNullException(nameof(methodName));
            }

            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            for (var i = 0; i < modifiers.Count; i++)
            {
                Write(modifiers[i]);
                Write(" ");
            }

            Write(returnType)
                .Write(" ")
                .Write(methodName)
                .Write("(")
                .Write(string.Join(", ", parameters.Select(p => p.typeName + " " + p.parameterName)))
                .WriteLine(")");

            return new CSharpBlockScope(this);
        }

        public LineDirectiveScope BuildLineDirective(SourceSpan? span)
        {
            return new LineDirectiveScope(this, span);
        }

        #region The Taylor ZONE

        public CodeWriter WriteStartAssignment(string name)
        {
            return Write(name).Write(" = ");
        }

        public CodeWriter WriteParameterSeparator()
        {
            return Write(", ");
        }

        public CodeWriter WriteStartNewObject(string typeName)
        {
            return Write("new ").Write(typeName).Write("(");
        }

        public CodeWriter WriteStartMethodCall(string methodName)
        {
            Write(methodName);

            return Write("(");
        }

        public CodeWriter WriteEndMethodCall()
        {
            return WriteEndMethodCall(endLine: true);
        }

        public CodeWriter WriteEndMethodCall(bool endLine)
        {
            Write(")");
            if (endLine)
            {
                WriteLine(";");
            }

            return this;
        }

        // Writes a method invocation for the given instance name.
        public CodeWriter WriteInstanceMethodCall(
            string instanceName,
            string methodName,
            params string[] parameters)
        {
            if (instanceName == null)
            {
                throw new ArgumentNullException(nameof(instanceName));
            }

            if (methodName == null)
            {
                throw new ArgumentNullException(nameof(methodName));
            }

            return WriteMethodCallExpressionStatement(
                string.Format(CultureInfo.InvariantCulture, InstanceMethodFormat, instanceName, methodName), 
                parameters);
        }

        public CodeWriter WriteStartInstanceMethodCall(string instanceName, string methodName)
        {
            if (instanceName == null)
            {
                throw new ArgumentNullException(nameof(instanceName));
            }

            if (methodName == null)
            {
                throw new ArgumentNullException(nameof(methodName));
            }

            return WriteStartMethodCall(
                string.Format(CultureInfo.InvariantCulture, InstanceMethodFormat, instanceName, methodName));
        }

        public CodeWriter WriteAssignmentStatement(string left, string right)
        {
            if (left == null)
            {
                throw new ArgumentNullException(nameof(left));
            }

            if (right == null)
            {
                throw new ArgumentNullException(nameof(right));
            }

            Write(left);
            Write(" = ");
            Write(right);
            WriteLine(";");

            return this;
        }

        #endregion

        public struct CSharpBlockScope : IDisposable
        {
            private CodeWriter _writer;
            private bool _autoSpace;
            private int _tabSize;
            private int _startIndent;

            public CSharpBlockScope(CodeWriter writer, int tabSize = 4, bool autoSpace = true)
            {
                _writer = writer;
                _autoSpace = true;
                _tabSize = tabSize;
                _startIndent = -1; // Set in WriteStartScope

                WriteStartScope();
            }

            public void Dispose()
            {
                WriteEndScope();
            }

            private void WriteStartScope()
            {
                TryAutoSpace(" ");

                _writer.WriteLine("{");
                _writer.CurrentIndent += _tabSize;
                _startIndent = _writer.CurrentIndent;
            }

            private void WriteEndScope()
            {
                TryAutoSpace(_writer.NewLine);

                // Ensure the scope hasn't been modified
                if (_writer.CurrentIndent == _startIndent)
                {
                    _writer.CurrentIndent -= _tabSize;
                }

                _writer.WriteLine("}");
            }

            private void TryAutoSpace(string spaceCharacter)
            {
                if (_autoSpace &&
                    _writer.Builder.Length > 0 &&
                    !char.IsWhiteSpace(_writer.Builder[_writer.Builder.Length - 1]))
                {
                    _writer.Write(spaceCharacter);
                }
            }
        }

        public struct CSharpStatementScope : IDisposable
        {
            private readonly CodeWriter _writer;

            public CSharpStatementScope(CodeWriter writer)
            {
                if (writer == null)
                {
                    throw new ArgumentNullException(nameof(writer));
                }

                _writer = writer;
            }

            public void Dispose()
            {
                _writer.WriteLine(";");
            }
        }

        public struct LineDirectiveScope : IDisposable
        { 
            private readonly CodeWriter _writer;
            private readonly int _startIndent;
            private readonly bool _hasLocation;

            public LineDirectiveScope(CodeWriter writer, SourceSpan? span)
            {
                if (writer == null)
                {
                    throw new ArgumentNullException(nameof(writer));
                }

                _writer = writer;
                _startIndent = _writer.CurrentIndent;
                _hasLocation = span?.FilePath != null;

                if (_hasLocation)
                {
                    _writer.CurrentIndent = 0;
                    WriteLineDirective(span.Value);
                }
            }

            private void WriteLineDirective(SourceSpan span)
            {
                if (_writer.Builder.Length >= _writer.NewLine.Length && !_writer.IsAfterNewLine)
                {
                    _writer.WriteLine();
                }

                var lineNumberAsString = (span.LineIndex + 1).ToString(CultureInfo.InvariantCulture);
                _writer.Write("#line ").Write(lineNumberAsString).Write(" \"").Write(span.FilePath).WriteLine("\"");
            }

            public void Dispose()
            {
                if (_hasLocation)
                {
                    // Need to add an additional line at the end IF there wasn't one already written.
                    // This is needed to work with the C# editor's handling of #line ...
                    var builder = _writer.Builder;
                    var endsWithNewline = builder.Length > 0 && builder[builder.Length - 1] == '\n';

                    // Always write at least 1 empty line to potentially separate code from pragmas.
                    _writer.WriteLine();

                    // Check if the previous empty line wasn't enough to separate code from pragmas.
                    if (!endsWithNewline)
                    {
                        _writer.WriteLine();
                    }

                    _writer.WriteLine("#line default");
                    _writer.WriteLine("#line hidden");
                }

                _writer.CurrentIndent = _startIndent;
            }
        }
    }
}
