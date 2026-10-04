namespace GenericVectorBuilder.Core.Contracts;

/// <summary>
/// Splits a document into the chunks that get embedded, each with a deterministic id.
/// Why an interface: table rows split well on line boundaries, but source code splits best on
/// its own structure (types, methods, properties). The pipeline only needs "document in, chunks
/// out", so a code chunker can be dropped in without the pipeline knowing what code is.
/// Ids must depend only on the pipeline, the document key and the chunk text, so an unchanged
/// chunk keeps its id across runs and a re-run upsert overwrites instead of duplicating.
/// </summary>
public interface IChunker
{
   /// <summary>
   /// Splits one document into chunks, in document order.
   /// </summary>
   /// <param name="pipeline">Pipeline name, part of every chunk id so pipelines never collide.</param>
   /// <param name="document">The document to split.</param>
   /// <returns>The chunks, in order, with 0-based ordinals.</returns>
   IReadOnlyList<Chunk> Split( string pipeline, Document document );
}

/// <summary>
/// Turns one raw source record into a document: the text to embed, a stable key and metadata.
/// Why an interface: a table row becomes "Column: value" lines keyed by a chosen column, while a
/// source file becomes its own content keyed by its path. The pipeline treats both the same way
/// once they are documents.
/// </summary>
public interface IDocumentMapper
{
   /// <summary>
   /// Maps one record to a document.
   /// </summary>
   /// <param name="record">The source record.</param>
   /// <returns>The document, or null when the record has no text worth embedding.</returns>
   Document? Map( SourceRecord record );
}
