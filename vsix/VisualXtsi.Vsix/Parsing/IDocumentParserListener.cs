namespace VisualXtsi.Parsing
{
    /// <summary>
    /// A consumer stored in its buffer's properties (under this interface's type) that wants
    /// the buffer's <see cref="DocumentParser"/> whenever one is created for it.
    /// </summary>
    internal interface IDocumentParserListener
    {
        void ParserCreated(DocumentParser parser);
    }
}
