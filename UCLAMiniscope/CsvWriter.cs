// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using Bonsai;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Xml.Serialization;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Writes any input sequence to a CSV file inside the active UCLA recording session folder.
    /// </summary>
    /// <remarks>
    /// Public fields and properties are flattened into columns. The schema is determined from
    /// the first element received during each recording session.
    /// </remarks>
    [DefaultProperty(nameof(FileName))]
    [Description("Writes input data to a CSV file inside the active UCLA recording session folder.")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class CsvWriter : Combinator
    {
        /// <summary>
        /// Gets or sets the folder name created inside the recording session folder.
        /// </summary>
        [Description("Folder name inside the recording session folder.")]
        public string FolderName { get; set; } = "Data";

        /// <summary>
        /// Gets or sets the CSV file name.
        /// </summary>
        [Description("Name of the CSV file. The .csv extension is added when omitted.")]
        public string FileName { get; set; } = "data.csv";

        /// <summary>
        /// Gets or sets the delimiter used to separate CSV columns.
        /// </summary>
        [Description("Delimiter used to separate CSV columns.")]
        public string Delimiter { get; set; } = ",";

        /// <summary>
        /// Gets or sets a value indicating whether column names are written as the first row.
        /// </summary>
        [Description("When true, writes column names as the first row.")]
        public bool IncludeHeader { get; set; } = true;

        /// <summary>
        /// Gets or sets a comma-delimited list of column names to use as the first row of the CSV file.
        /// This overrides the default behavior of using variable names as column headers. If empty, variable names will be used.
        /// The string will not be validated against the actual data, so ensure that the number of column names matches the number of columns in the data.
        /// </summary>
        [Description("Comma-delimited list of column names. If empty, variable names will be used.\n" +
                     "Warning: this will not be validated against the actual data.")]
        public string Header { get; set; } = "";

        /// <summary>
        /// Writes source elements received while a UCLA recording session is active and passes
        /// the complete source sequence through unchanged.
        /// </summary>
        /// <typeparam name="TSource">The type of data to write.</typeparam>
        /// <param name="source">The source sequence.</param>
        /// <returns>The source sequence, unchanged.</returns>
        public override IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            return Observable.Create<TSource>(observer =>
            {
                var gate = new object();
                StreamWriter writer = null;
                CsvSchema schema = null;
                string activePath = null;

                void CloseWriter()
                {
                    lock (gate)
                    {
                        writer?.Dispose();
                        writer = null;
                        schema = null;
                        activePath = null;
                    }
                }

                var subscription = source.Subscribe(
                    input =>
                    {
                        try
                        {
                            lock (gate)
                            {
                                if (!RecordingService.IsRecording)
                                {
                                    writer?.Dispose();
                                    writer = null;
                                    schema = null;
                                    activePath = null;
                                }
                                else
                                {
                                    string outputPath = GetOutputPath();
                                    if (writer == null ||
                                        !string.Equals(activePath, outputPath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        writer?.Dispose();
                                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

                                        schema = CsvSchema.Create(typeof(TSource), input);
                                        writer = new StreamWriter(
                                            outputPath,
                                            append: false,
                                            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                                        activePath = outputPath;

                                        if (IncludeHeader)
                                            writer.WriteLine(schema.GetHeader(Delimiter));
                                    }

                                    writer.WriteLine(schema.GetRow(input, Delimiter));
                                }
                            }

                            observer.OnNext(input);
                        }
                        catch (Exception ex)
                        {
                            CloseWriter();
                            observer.OnError(ex);
                        }
                    },
                    ex =>
                    {
                        CloseWriter();
                        observer.OnError(ex);
                    },
                    () =>
                    {
                        CloseWriter();
                        observer.OnCompleted();
                    });

                return new CompositeDisposable(
                    subscription,
                    Disposable.Create(CloseWriter));
            });
        }

        string GetOutputPath()
        {
            if (string.IsNullOrWhiteSpace(FolderName))
                throw new InvalidOperationException("A valid folder name must be specified.");
            if (string.IsNullOrWhiteSpace(FileName))
                throw new InvalidOperationException("A valid CSV file name must be specified.");
            if (string.IsNullOrEmpty(Delimiter))
                throw new InvalidOperationException("A CSV delimiter must be specified.");

            ValidatePathSegment(FolderName, nameof(FolderName));
            ValidatePathSegment(FileName, nameof(FileName));

            string fileName = Path.GetExtension(FileName).Length == 0
                ? FileName + ".csv"
                : FileName;

            return Path.Combine(
                MouseInfoService.RootPath,
                MouseInfoService.MouseID,
                RecordingService.Date,
                RecordingService.Time,
                FolderName,
                fileName);
        }

        static void ValidatePathSegment(string value, string propertyName)
        {
            if (Path.IsPathRooted(value) ||
                value == "." ||
                value == ".." ||
                !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{propertyName} must be a name, not a path.");
            }
        }

        sealed class CsvSchema
        {
            readonly IReadOnlyList<CsvColumn> columns;

            CsvSchema(IReadOnlyList<CsvColumn> columns)
            {
                this.columns = columns;
            }

            public static CsvSchema Create(Type declaredType, object sample)
            {
                var columns = new List<CsvColumn>();
                Type dataType = sample?.GetType() ?? declaredType;
                AddColumns(
                    dataType,
                    sample,
                    string.Empty,
                    value => value,
                    new HashSet<Type>(),
                    columns);

                if (columns.Count == 0)
                    columns.Add(new CsvColumn("Value", value => value));

                return new CsvSchema(columns);
            }

            public string GetHeader(string delimiter)
            {
                return string.Join(
                    delimiter,
                    columns.Select(column => Escape(column.Name, delimiter)));
            }

            public string GetRow(object value, string delimiter)
            {
                return string.Join(
                    delimiter,
                    columns.Select(column => Escape(FormatValue(column.GetValue(value)), delimiter)));
            }

            static void AddColumns(
                Type type,
                object sample,
                string path,
                Func<object, object> accessor,
                HashSet<Type> ancestors,
                ICollection<CsvColumn> columns)
            {
                type = sample?.GetType() ?? type;

                if (IsScalar(type) || ancestors.Contains(type))
                {
                    columns.Add(new CsvColumn(
                        string.IsNullOrEmpty(path) ? "Value" : path,
                        accessor));
                    return;
                }

                if (sample is IList list)
                {
                    Type elementType = GetElementType(type);
                    for (int index = 0; index < list.Count; index++)
                    {
                        int itemIndex = index;
                        object itemSample = list[index];
                        Type itemType = itemSample?.GetType() ?? elementType ?? typeof(object);
                        string itemPath = string.IsNullOrEmpty(path)
                            ? $"Item{index}"
                            : $"{path}.Item{index}";

                        AddColumns(
                            itemType,
                            itemSample,
                            itemPath,
                            root =>
                            {
                                var currentList = accessor(root) as IList;
                                return currentList != null && itemIndex < currentList.Count
                                    ? currentList[itemIndex]
                                    : null;
                            },
                            ancestors,
                            columns);
                    }

                    if (list.Count == 0)
                    {
                        columns.Add(new CsvColumn(
                            string.IsNullOrEmpty(path) ? "Value" : path,
                            accessor));
                    }

                    return;
                }

                if (typeof(IList).IsAssignableFrom(type))
                {
                    columns.Add(new CsvColumn(
                        string.IsNullOrEmpty(path) ? "Value" : path,
                        accessor));
                    return;
                }

                var members = GetDataMembers(type).ToArray();
                if (members.Length == 0)
                {
                    columns.Add(new CsvColumn(
                        string.IsNullOrEmpty(path) ? "Value" : path,
                        accessor));
                    return;
                }

                var nestedAncestors = new HashSet<Type>(ancestors) { type };
                foreach (MemberInfo member in members)
                {
                    Func<object, object> parentAccessor = accessor;
                    object memberSample = sample == null ? null : GetMemberValue(member, sample);
                    Type memberType = GetMemberType(member);
                    string memberPath = string.IsNullOrEmpty(path)
                        ? member.Name
                        : $"{path}.{member.Name}";

                    AddColumns(
                        memberType,
                        memberSample,
                        memberPath,
                        root =>
                        {
                            object parent = parentAccessor(root);
                            return parent == null ? null : GetMemberValue(member, parent);
                        },
                        nestedAncestors,
                        columns);
                }
            }

            static IEnumerable<MemberInfo> GetDataMembers(Type type)
            {
                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;

                var fields = type
                    .GetFields(Flags)
                    .Where(field => !field.IsStatic)
                    .Cast<MemberInfo>();

                var properties = type
                    .GetProperties(Flags)
                    .Where(property =>
                        property.CanRead &&
                        property.GetMethod != null &&
                        !property.GetMethod.IsStatic &&
                        property.GetIndexParameters().Length == 0)
                    .Cast<MemberInfo>();

                return fields
                    .Concat(properties)
                    .Where(member => !member.IsDefined(typeof(XmlIgnoreAttribute), inherit: true))
                    .OrderBy(member => member.MetadataToken);
            }

            static Type GetElementType(Type type)
            {
                if (type.IsArray) return type.GetElementType();

                return type
                    .GetInterfaces()
                    .Concat(new[] { type })
                    .Where(candidate => candidate.IsGenericType)
                    .Where(candidate => candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    .Select(candidate => candidate.GetGenericArguments()[0])
                    .FirstOrDefault();
            }

            static Type GetMemberType(MemberInfo member)
            {
                return member is FieldInfo field
                    ? field.FieldType
                    : ((PropertyInfo)member).PropertyType;
            }

            static object GetMemberValue(MemberInfo member, object value)
            {
                return member is FieldInfo field
                    ? field.GetValue(value)
                    : ((PropertyInfo)member).GetValue(value);
            }

            static bool IsScalar(Type type)
            {
                type = Nullable.GetUnderlyingType(type) ?? type;
                return type.IsPrimitive ||
                       type.IsEnum ||
                       type == typeof(string) ||
                       type == typeof(decimal) ||
                       type == typeof(DateTime) ||
                       type == typeof(DateTimeOffset) ||
                       type == typeof(TimeSpan) ||
                       type == typeof(Guid) ||
                       type == typeof(IntPtr) ||
                       type == typeof(UIntPtr);
            }

            static string FormatValue(object value)
            {
                if (value == null) return string.Empty;

                switch (value)
                {
                    case float single:
                        return single.ToString("G9", CultureInfo.InvariantCulture);
                    case double number:
                        return number.ToString("G17", CultureInfo.InvariantCulture);
                    case DateTime dateTime:
                        return dateTime.ToString("o", CultureInfo.InvariantCulture);
                    case DateTimeOffset dateTimeOffset:
                        return dateTimeOffset.ToString("o", CultureInfo.InvariantCulture);
                    case TimeSpan timeSpan:
                        return timeSpan.ToString("c", CultureInfo.InvariantCulture);
                    case string text:
                        return text;
                    case IEnumerable sequence:
                        return string.Join(
                            ";",
                            sequence.Cast<object>().Select(FormatValue));
                    case IFormattable formattable:
                        return formattable.ToString(null, CultureInfo.InvariantCulture);
                    default:
                        return value.ToString();
                }
            }

            static string Escape(string value, string delimiter)
            {
                if (value == null) return string.Empty;

                bool requiresQuotes =
                    value.IndexOf('"') >= 0 ||
                    value.IndexOf('\r') >= 0 ||
                    value.IndexOf('\n') >= 0 ||
                    value.IndexOf(delimiter, StringComparison.Ordinal) >= 0;

                return requiresQuotes
                    ? "\"" + value.Replace("\"", "\"\"") + "\""
                    : value;
            }
        }

        sealed class CsvColumn
        {
            readonly Func<object, object> accessor;

            public CsvColumn(string name, Func<object, object> accessor)
            {
                Name = name;
                this.accessor = accessor;
            }

            public string Name { get; }

            public object GetValue(object value)
            {
                return accessor(value);
            }
        }
    }
}
