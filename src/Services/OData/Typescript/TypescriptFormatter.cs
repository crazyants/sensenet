﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using SenseNet.ContentRepository.Schema.Metadata;
using SenseNet.ContentRepository.Storage;
using SenseNet.Portal.Virtualization;

namespace SenseNet.Portal.OData.Typescript
{
    public class TypescriptFormatter : JsonFormatter
    {
        public override string FormatName { get { return "typescript"; } }
        public override string MimeType { get { return "text/x-typescript"; } }

        protected override void WriteMetadata(System.IO.TextWriter writer, Metadata.Edmx edmx)
        {
            var requestedModule = HttpContext.Current.Request["module"]?.ToLowerInvariant();
            if (string.IsNullOrEmpty(requestedModule))
                requestedModule = "classes";

            var schema0 = new ContentRepository.Schema.Metadata.Schema(new[] { "Application", "ApplicationCacheFile", "FieldSettingContent", "JournalNode" });
            var context = new TypescriptGenerationContext();
            var schema1 = new TypescriptTypeCollectorVisitor(context).Visit(schema0);

            switch (requestedModule)
            {
                case "enums":
                    new TypescriptEnumsVisitor(context, writer).Visit(schema1);
                    break;
                case "complextypes":
                    new TypescriptComplexTypesVisitor(context, writer).Visit(schema1);
                    break;
                case "contenttypes":
                    new TypescriptClassesVisitor(context, writer).Visit(schema1);
                    break;
                case "resources":
                    ResourceWriter.WriteResourceClasses(writer);
                    break;
                case "schemas":
                    new TypescriptCtdVisitor(context, writer).Visit(schema1);
                    break;
                case "fieldsettings":
                    new TypescriptFieldSettingsVisitor(context, writer).Visit(schema1);
                    break;
                default:
                    throw new InvalidOperationException("Unknown module name: " + requestedModule
                        + ". Valid names: enums, complextypes, contenttypes, resources, schemas, fieldsettings.");
            }
        }
        protected override void WriteServiceDocument(PortalContext portalContext, IEnumerable<string> names) { throw new SnNotSupportedException(); }
        protected override void WriteSingleContent(PortalContext portalContext, Dictionary<string, object> fields) { throw new SnNotSupportedException(); }
        protected override void WriteActionsProperty(PortalContext portalContext, ODataActionItem[] actions, bool raw) { throw new SnNotSupportedException(); }
        protected override void WriteOperationCustomResult(PortalContext portalContext, object result, int? allCount) { throw new SnNotSupportedException(); }
        protected override void WriteMultipleContent(PortalContext portalContext, List<Dictionary<string, object>> contents, int count) { throw new SnNotSupportedException(); }
        protected override void WriteCount(PortalContext portalContext, int count)
        {
            WriteRaw(count, portalContext);
        }
    }
}
