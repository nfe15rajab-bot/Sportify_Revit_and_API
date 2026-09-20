using System.Collections.Specialized;

namespace SportfyRevit
{
    // RoofBoundaryServer hands the workspace paths (the Sportify folder, the analyses, the charts, the PDFs) to WorkspaceEndpoints, which needs the analyses, QuestPDF and
    // the Revit-free half of the add-in: ContractCheck compiles the real one and tests it. This check only needs the server to compile, so it gets a stand-in that owns no path.
    internal sealed class EndpointResponse
    {
        public int Status = 200;
        public string ContentType = "application/json";
        public byte[]? Body;
        public string? FilePath;
    }

    internal static class WorkspaceEndpoints
    {
        public static EndpointResponse? Handle(string method, string? path, NameValueCollection query, Func<byte[]> readBody) => null;
        public static string ContentTypeOf(string path) => "application/octet-stream";
    }
}
