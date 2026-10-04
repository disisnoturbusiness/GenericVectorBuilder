using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Builds the Vespa application package the sink deploys: services.xml, hosts.xml and one schema
/// file per collection.
/// Why this exists: Vespa does not create document types on the fly. Every collection needs a schema
/// with its tensor length written into it, and the schema set only changes by deploying a new
/// package to the config server. The sink therefore reads the deployed schemas back, adds or
/// removes one, and deploys the result.
/// Why a keepalive schema is always present: a content cluster must list at least one document type, so
/// dropping the last collection would otherwise be refused. Its name has capital letters, which no
/// pipeline collection name (lowercase letters, digits and underscores) can have.
/// </summary>
internal static class VespaApplicationPackage
{
   #region Data Members

   /// <summary>Name of the schema that keeps the content cluster valid when no collection exists.</summary>
   public const string KEEPALIVE = "GvbKeepalive";

   /// <summary>The single tensor dimension is the first "x[n]" inside the embedding field.</summary>
   private static readonly Regex DIMENSION_PATTERN = new( @"tensor<float>\(x\[(\d+)\]\)", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The schema text for one collection: summary fields for the payload and an HNSW-indexed
   /// tensor attribute for the vector.
   /// Index settings: distance-metric prenormalized-angular (cosine for unit-length vectors, using
   /// a plain dot product), max-links-per-node 16 (the HNSW m), neighbors-to-explore-at-insert 128
   /// (ef_construction). The default rank profile scores a hit as 1 - distance, which for this
   /// metric is the cosine similarity. The query input is named by vector length (see
   /// <see cref="QueryInput"/>) so collections of different lengths can share one application.
   /// Why the metadata is one JSON string: schema fields are fixed at deploy time, so arbitrary
   /// meta_* keys cannot become fields.
   /// </summary>
   /// <param name="name">Document type and schema name.</param>
   /// <param name="dimension">Embedding dimension.</param>
   /// <returns>The .sd file text.</returns>
   public static string Schema( string name, int dimension )
   {
      return $$"""
         schema {{name}} {
             document {{name}} {
                 field doc_key type string {
                     indexing: summary
                 }
                 field table type string {
                     indexing: summary
                 }
                 field origin type string {
                     indexing: summary
                 }
                 field ordinal type int {
                     indexing: summary
                 }
                 field text type string {
                     indexing: summary
                 }
                 field meta type string {
                     indexing: summary
                 }
                 field embedding type tensor<float>(x[{{dimension}}]) {
                     indexing: attribute | index
                     attribute {
                         distance-metric: prenormalized-angular
                     }
                     index {
                         hnsw {
                             max-links-per-node: 16
                             neighbors-to-explore-at-insert: 128
                         }
                     }
                 }
             }
             rank-profile default {
                 inputs {
                     query({{QueryInput( dimension )}}) tensor<float>(x[{{dimension}}])
                 }
                 first-phase {
                     expression: 1 - distance(field, embedding)
                 }
             }
         }
         """;
   }

   /// <summary>
   /// The name of the query input that carries the search vector for a given vector length, e.g.
   /// "q1024".
   /// Why the length is in the name: Vespa keeps one type per query input name for the whole
   /// application. Two collections with different vector lengths that both used "q" would make every
   /// search fail with "Conflicting input type declarations", so each length gets its own input name.
   /// </summary>
   /// <param name="dimension">Embedding dimension.</param>
   /// <returns>The input name, without the query( ) wrapper.</returns>
   public static string QueryInput( int dimension )
   {
      return $"q{dimension}";
   }

   /// <summary>
   /// Reads the vector length out of a deployed schema's text.
   /// </summary>
   /// <param name="schemaText">The .sd file text.</param>
   /// <returns>The dimension, or null when the schema has no tensor.</returns>
   public static int? Dimension( string schemaText )
   {
      Match match = DIMENSION_PATTERN.Match( schemaText );
      return match.Success ? int.Parse( match.Groups[1].Value ) : null;
   }

   /// <summary>
   /// Zips a complete application package for the given schemas.
   /// </summary>
   /// <param name="schemas">Schema text by schema name. The keepalive schema is added when missing.</param>
   /// <param name="allowRemoval">True when this deployment removes a schema, which Vespa refuses
   /// unless the package says deleting that data is allowed (validation-overrides.xml).</param>
   /// <returns>The zip file bytes.</returns>
   public static byte[] Build( IReadOnlyDictionary<string, string> schemas, bool allowRemoval )
   {
      var all = new SortedDictionary<string, string>( schemas.ToDictionary( p => p.Key, p => p.Value ), StringComparer.Ordinal );
      all.TryAdd( KEEPALIVE, $"schema {KEEPALIVE} {{\n    document {KEEPALIVE} {{\n        field note type string {{\n            indexing: summary\n        }}\n    }}\n}}\n" );

      using var stream = new MemoryStream();
      using( var zip = new ZipArchive( stream, ZipArchiveMode.Create, true ) )
      {
         AddEntry( zip, "services.xml", Services( all.Keys ) );
         AddEntry( zip, "hosts.xml", Hosts() );
         foreach( KeyValuePair<string, string> schema in all )
         {
            AddEntry( zip, $"schemas/{schema.Key}.sd", schema.Value );
         }

         if( allowRemoval )
         {
            AddEntry( zip, "validation-overrides.xml", ValidationOverrides() );
         }
      }

      return stream.ToArray();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// services.xml for a single node: one container for queries and feeding, one content cluster
   /// holding every schema, no replication.
   /// </summary>
   /// <param name="names">Document type names.</param>
   /// <returns>The file text.</returns>
   private static string Services( IEnumerable<string> names )
   {
      var documents = new StringBuilder();
      foreach( string name in names )
      {
         documents.Append( $"      <document type=\"{name}\" mode=\"index\"/>\n" );
      }

      return $"""
         <?xml version="1.0" encoding="utf-8" ?>
         <services version="1.0">
           <container id="default" version="1.0">
             <document-api/>
             <search/>
             <nodes>
               <node hostalias="node1"/>
             </nodes>
           </container>
           <content id="content" version="1.0">
             <min-redundancy>1</min-redundancy>
             <documents>
         {documents}    </documents>
             <nodes>
               <node hostalias="node1" distribution-key="0"/>
             </nodes>
           </content>
         </services>

         """;
   }

   /// <summary>
   /// hosts.xml naming the one host the Docker container runs on.
   /// </summary>
   /// <returns>The file text.</returns>
   private static string Hosts()
   {
      return """
         <?xml version="1.0" encoding="utf-8" ?>
         <hosts>
           <host name="localhost">
             <alias>node1</alias>
           </host>
         </hosts>

         """;
   }

   /// <summary>
   /// Allows removing a schema (and its data) for the next two days. Vespa limits an override to
   /// 30 days and counts the date as UTC, so two days covers a deploy that straddles midnight.
   /// </summary>
   /// <returns>The file text.</returns>
   private static string ValidationOverrides()
   {
      string until = DateTime.UtcNow.AddDays( 2 ).ToString( "yyyy-MM-dd" );
      return $"<validation-overrides>\n  <allow until=\"{until}\">schema-removal</allow>\n</validation-overrides>\n";
   }

   /// <summary>
   /// Adds one text file to the zip.
   /// </summary>
   /// <param name="zip">The archive.</param>
   /// <param name="path">Path inside the package.</param>
   /// <param name="text">File text.</param>
   private static void AddEntry( ZipArchive zip, string path, string text )
   {
      ZipArchiveEntry entry = zip.CreateEntry( path );
      using var writer = new StreamWriter( entry.Open(), new UTF8Encoding( false ) );
      writer.Write( text );
   }

   #endregion Private Methods
}
