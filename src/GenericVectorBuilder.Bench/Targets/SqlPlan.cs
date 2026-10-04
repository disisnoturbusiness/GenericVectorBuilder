using System.Xml.Linq;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Reads a SQL Server execution plan (the XML from SET STATISTICS XML ON) into the few facts the
/// benchmark reports: which operators ran, on which table and index, and how many rows each read.
/// Why the plan and not the query text: whether a search used the DiskANN index is decided by
/// the optimiser, and the plan is the server's own record of what it executed.
/// </summary>
public static class SqlPlan
{
   #region Data Members

   private static readonly XNamespace SHOWPLAN = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Lists every operator of a plan with its table, index and actual row count.
   /// </summary>
   /// <param name="xml">The plan XML.</param>
   /// <returns>The operators in plan order.</returns>
   public static PlanFacts Parse( string xml )
   {
      XDocument document = XDocument.Parse( xml );
      var operators = new List<PlanOperator>();
      foreach( XElement relOp in document.Descendants( SHOWPLAN + "RelOp" ) )
      {
         XElement? target = relOp.Element( SHOWPLAN + "IndexScan" )?.Element( SHOWPLAN + "Object" );
         long? rows = relOp.Element( SHOWPLAN + "RunTimeInformation" ) is XElement run
            ? run.Elements( SHOWPLAN + "RunTimeCountersPerThread" ).Sum( c => (long?)long.Parse( (string?)c.Attribute( "ActualRows" ) ?? "0" ) )
            : null;
         operators.Add( new PlanOperator( (string?)relOp.Attribute( "PhysicalOp" ) ?? "?", Unquote( (string?)target?.Attribute( "Database" ) ), Unquote( (string?)target?.Attribute( "Table" ) ),
            Unquote( (string?)target?.Attribute( "Index" ) ), (string?)target?.Attribute( "IndexKind" ), rows ) );
      }

      return new PlanFacts( operators );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Removes the square brackets SQL Server puts around names in a plan.
   /// </summary>
   /// <param name="name">Name such as "[gvb_x]", or null.</param>
   /// <returns>The bare name, or null.</returns>
   private static string? Unquote( string? name )
   {
      return name?.Trim( '[', ']' );
   }

   #endregion Private Methods
}

/// <summary>
/// One operator of an execution plan.
/// </summary>
/// <param name="PhysicalOp">Operator name, e.g. "Vector Index Seek" or "Clustered Index Scan".</param>
/// <param name="Database">Database of the table, or null for an operator that reads none.</param>
/// <param name="Table">Table it reads, or null for an operator that reads none.</param>
/// <param name="Index">Index it reads, or null.</param>
/// <param name="IndexKind">"DiskANN", "Clustered" and so on, or null.</param>
/// <param name="ActualRows">Rows it produced when the plan is an actual plan, else null.</param>
public sealed record PlanOperator( string PhysicalOp, string? Database, string? Table, string? Index, string? IndexKind, long? ActualRows );

/// <summary>
/// The operators of one execution plan, with the questions the benchmark asks of it.
/// </summary>
/// <param name="Operators">Every operator.</param>
public sealed record PlanFacts( IReadOnlyList<PlanOperator> Operators )
{
   /// <summary>The vector index operator, or null when the plan uses no vector index.</summary>
   public PlanOperator? VectorSeek => Operators.FirstOrDefault( o => o.PhysicalOp == "Vector Index Seek" || o.IndexKind == "DiskANN" );

   /// <summary>The operator that read the whole table, or null when none did.</summary>
   public PlanOperator? Scan => Operators.FirstOrDefault( o => o.PhysicalOp is "Clustered Index Scan" or "Table Scan" or "Index Scan" );

   /// <summary>
   /// Plain-English summary of the plan's table access for the report.
   /// </summary>
   /// <returns>One clause naming the operator, table, index and rows.</returns>
   public string Describe()
   {
      if( VectorSeek is PlanOperator seek )
      {
         return $"{seek.PhysicalOp} on index {seek.Index} of {seek.Database}.{seek.Table} (IndexKind {seek.IndexKind}), {seek.ActualRows?.ToString( "N0" ) ?? "?"} rows returned";
      }

      return Scan is PlanOperator scan
         ? $"{scan.PhysicalOp} of {scan.Database}.{scan.Table} through {scan.Index} (IndexKind {scan.IndexKind}), {scan.ActualRows?.ToString( "N0" ) ?? "?"} rows read, no vector index operator"
         : "no table access operator found";
   }
}
