using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Folke.Elm.Mapping;
using Folke.Elm.Visitor;

namespace Folke.Elm
{
    public class BaseQueryBuilder<T> : BaseQueryBuilder, IQueryableCommand<T>
    {
        public BaseQueryBuilder(IFolkeConnection connection)
            : base(connection, typeof(T))
        {
        }

        protected BaseQueryBuilder(IDatabaseDriver databaseDriver, IMapper mapper)
            : base(databaseDriver, mapper, typeof(T))
        {
        }

        public IEnumerator<T> GetEnumerator()
        {
            return this.Enumerate().GetEnumerator();
        }
    }

    public class BaseQueryBuilder : IQueryableCommand
    {
        private readonly SqlStringBuilder query;
        private IList<object> parameters;

        private MappedClass baseMappedClass;

        private readonly IList<SelectedField> selectedFields = new List<SelectedField>();
        private readonly IList<SelectedTable> tables;

        private SelectedTable defaultTable;
        private readonly TypeMapping defaultType;
        private readonly Type parametersType;

        internal SelectedTable DefaultTable => defaultTable;
        internal IList<SelectedField> SelectedFields => selectedFields;

        public BaseQueryBuilder(IFolkeConnection connection, Type type = null, Type parametersType = null)
            : this(connection.Driver, connection.Mapper, type, parametersType)
        {
            Connection = connection;
        }

        public BaseQueryBuilder(IDatabaseDriver databaseDriver, IMapper mapper, Type defaultType, Type parametersType = null)
            : this(databaseDriver, mapper)
        {
            this.defaultType = Mapper.GetTypeMapping(defaultType);
            this.parametersType = parametersType;
        }

        public BaseQueryBuilder(IDatabaseDriver databaseDriver, IMapper mapper):this(databaseDriver.CreateSqlStringBuilder())
        {
            Mapper = mapper;
            Driver = databaseDriver;
        }

        public IMapper Mapper { get; set; }

        public BaseQueryBuilder(BaseQueryBuilder parentBuilder):this(parentBuilder.Driver, parentBuilder.Mapper)
        {
            if (parentBuilder.parameters == null)
                parentBuilder.parameters = new List<object>();
            parameters = parentBuilder.parameters;
            tables = parentBuilder.tables;
            defaultTable = parentBuilder.defaultTable;
            defaultType = parentBuilder.defaultType;
        }

        public BaseQueryBuilder(SqlStringBuilder stringBuilder = null)
        {
            query = stringBuilder ?? new SqlStringBuilder();
            tables = new List<SelectedTable>();
        }

        public string Sql => query.ToString();

        public IFolkeConnection Connection { get; }

        internal IDatabaseDriver Driver { get; }

        public object[] Parameters => parameters?.ToArray();

        public MappedClass MappedClass => baseMappedClass ?? (baseMappedClass = MappedClass.MapClass(selectedFields, defaultType, DefaultTable));

        /// <summary>
        /// Gets a table by its alias
        /// </summary>
        /// <param name="alias">The expression that is used as an alias to a table</param>
        /// <param name="register">Register the table if it has never been</param>
        /// <returns>The table</returns>
        protected internal SelectedTable GetTable(Expression alias, bool register)
        {
                    /*    Type type;
            var internalIdentifier = CreateTableInternalIdentifier(alias, out type);
            return tables.SingleOrDefault(t => t.InternalIdentifier == internalIdentifier);*/
            SelectedTable table;
            switch (alias.NodeType)
            {
                case ExpressionType.MemberAccess:
                    if (!Mapper.IsMapped(alias.Type)) return null;
                    var mapping = Mapper.GetTypeMapping(alias.Type);
                    var memberExpression = (MemberExpression)alias;
                    var parentTable = GetTable(memberExpression.Expression, false);
                    if (parentTable == null)
                    {
                        return GetRootTable(mapping, alias, register);
                    }
                    var propertyMapping = parentTable.Mapping.GetColumn(memberExpression.Member);
                    var selectedField =
                        selectedFields.FirstOrDefault(
                            x =>
                                x.Table == parentTable &&
                                x.PropertyMapping == propertyMapping);

                    if (selectedField == null)
                    {
                        selectedField = SelectField(propertyMapping, parentTable);
                    }

                    table = tables.FirstOrDefault(x => x.Parent == selectedField);
                    if (table == null)
                    {
                        if (!register)
                            throw new Exception($"Table for expression {alias} not registered");
                        table = RegisterTable(selectedField, mapping);
                    }
                    break;
                case ExpressionType.Parameter:
                    if (defaultTable == null)
                    {
                        defaultTable = new SelectedTable
                        {
                            Alias = "t",
                            Mapping = defaultType,
                            Parent = null
                        };
                    }
                    return defaultTable;
                case ExpressionType.Convert:
                    return GetTable(((UnaryExpression) alias).Operand, register);
                default:
                    return GetRootTable(Mapper.GetTypeMapping(alias.Type), alias, register);
            }

            return table;
        }

        private SelectedTable GetRootTable(TypeMapping mapping, Expression expression, bool register)
        {
            var expressionString = expression.ToString();
            var table = tables.FirstOrDefault(x => x.Expression == expressionString);
            if (table != null || !register) return table;
            table = new SelectedTable
            {
                Mapping = mapping,
                Alias = "t" + tables.Count,
                Expression = expressionString
            };
            tables.Add(table);
            return table;
        }

        internal SelectedTable RegisterTable(SelectedField parentField, TypeMapping mapping)
        {
            var table = new SelectedTable
            {
                Parent = parentField,
                Mapping = mapping,
                Alias = "t" + tables.Count
            };
            tables.Add(table);
            parentField.ChildTable = table;
            return table;
        }

        public int AddParameter(object parameter)
        {
            if (parameters == null)
                parameters = new List<object>();

            var parameterIndex = parameters.Count;
            if (parameter != null)
            {
                var parameterType = parameter.GetType();
                if (parameterType == typeof (TimeSpan))
                {
                    parameter = ((TimeSpan) parameter).TotalSeconds;
                }
                else if (parameterType.GetTypeInfo().IsEnum && parameterType.GetTypeInfo().GetCustomAttribute(typeof(FlagsAttribute)) != null)
                {
                    parameter = Convert.ChangeType(parameter, Enum.GetUnderlyingType(parameterType));
                }
                else if (Mapper.IsMapped(parameterType))
                {
                    var key = Mapper.GetTypeMapping(parameterType).Key;
                    parameter = key.PropertyInfo.GetValue(parameter);
                    if (parameter.Equals(0))
                        throw new Exception("Id should not be 0");
                }
            }

            parameters.Add(parameter);
            return parameterIndex;
        }

        internal void AddBooleanExpression(Expression expression, bool registerTable = false)
        {
            var lambda = expression as LambdaExpression;
            if (lambda != null)
            {
                AddExpression(lambda.Body, registerTable);
                return;
            }

            var visitable = ParseBooleanExpression(expression, registerTable);
            visitable.Accept(query);
        }

        internal IVisitable ParseBooleanExpression(Expression expression, bool registerTable = false)
        {
            if ((expression is MemberExpression || expression is ParameterExpression) && !Driver.HasBooleanType)
            {
                return new BinaryOperator(BinaryOperatorType.Equal, ParseExpression(expression, registerTable),
                    new ConstantNumber(1));
            }
            else
            {
                return ParseExpression(expression, registerTable);
            }
        }

        internal void AddExpression(Expression expression, bool registerTable = false)
        {
            var visitable = ParseExpression(expression, registerTable);
            visitable.Accept(query);
        }

        internal IVisitable ParseExpression(Expression expression, bool registerTable = false)
        {
            if (expression.NodeType == ExpressionType.Constant)
            {
                var constantExpression = (ConstantExpression) expression;
                if (constantExpression.Value == null)
                    return new Null();
                if (constantExpression.Type.GetTypeInfo().IsEnum)
                {
                    var enumType = constantExpression.Type;
                    var enumIndex = (int)constantExpression.Value;
                    return new Parameter(AddParameter(Enum.GetValues(enumType).GetValue(enumIndex)));
                }
            }

            var lambda = expression as LambdaExpression;
            if (lambda != null)
            {
                return ParseExpression(lambda.Body, registerTable);
            }

            var unary = expression as UnaryExpression;
            if (unary != null)
            {
                UnaryOperatorType unaryOperatorType;
                switch (unary.NodeType)
                {
                    case ExpressionType.Negate:
                        unaryOperatorType = UnaryOperatorType.Negate;
                        break;
                    case ExpressionType.Not:
                        unaryOperatorType = UnaryOperatorType.Not;
                        break;
                    case ExpressionType.Convert:
                    case ExpressionType.Quote:
                        return ParseExpression(unary.Operand, registerTable);
                    default:
                        throw new Exception("ExpressionType in UnaryExpression not supported");
                }

                var subExpression = unary.NodeType == ExpressionType.Not ?
                    ParseBooleanExpression(unary.Operand, registerTable)
                    : ParseExpression(unary.Operand, registerTable);
                return new UnaryOperator(unaryOperatorType, subExpression);
            }

            var binary = expression as BinaryExpression;
            if (binary != null)
            {
                bool booleanOperator;
                switch (binary.NodeType)
                {
                    case ExpressionType.AndAlso:
                    case ExpressionType.OrElse:
                        booleanOperator = true;
                        break;
                    default:
                        booleanOperator = false;
                        break;
                }

                var left = booleanOperator ? 
                                      ParseBooleanExpression(binary.Left, registerTable) 
                                      : ParseExpression(binary.Left, registerTable);
                
                BinaryOperatorType type;

                switch (binary.NodeType)
                {
                    case ExpressionType.Add:
                        type = BinaryOperatorType.Add;
                        break;
                    case ExpressionType.And:
                        type = BinaryOperatorType.And;
                        break;
                    case ExpressionType.AndAlso:
                        type = BinaryOperatorType.AndAlso;
                        break;
                    case ExpressionType.Divide:
                        type = BinaryOperatorType.Divide;
                        break;
                    case ExpressionType.Equal:
                        type = BinaryOperatorType.Equal;
                        break;
                    case ExpressionType.GreaterThan:
                        type = BinaryOperatorType.GreaterThan;
                        break;
                    case ExpressionType.GreaterThanOrEqual:
                        type = BinaryOperatorType.GreaterThanOrEqual;
                        break;
                    case ExpressionType.LessThan:
                        type = BinaryOperatorType.LessThan;
                        break;
                    case ExpressionType.LessThanOrEqual:
                        type = BinaryOperatorType.LessThanOrEqual;
                        break;
                    case ExpressionType.Modulo:
                        type = BinaryOperatorType.Modulo;
                        break;
                    case ExpressionType.Multiply:
                        type = BinaryOperatorType.Multiply;
                        break;
                    case ExpressionType.NotEqual:
                        type = BinaryOperatorType.NotEqual;
                        break;
                    case ExpressionType.Or:
                        type = BinaryOperatorType.Or;
                        break;
                    case ExpressionType.OrElse:
                        type = BinaryOperatorType.OrElse;
                        break;
                    case ExpressionType.Subtract:
                        type = BinaryOperatorType.Subtract;
                        break;
                    default:
                        throw new Exception("Expression type not supported");
                }

                var right = booleanOperator ? 
                                       ParseBooleanExpression(binary.Right, registerTable)
                                       : ParseExpression(binary.Right, registerTable);

                if (binary.Left.NodeType == ExpressionType.Convert && binary.Left.NodeType == ExpressionType.Convert
                    && ((UnaryExpression)binary.Left).Operand.Type.GetTypeInfo().IsEnum)
                {
                    var parameter = right as Parameter;
                    if (parameter != null)
                    {
                        parameters[parameter.Index] =
                            Enum.GetValues(((UnaryExpression) binary.Left).Operand.Type)
                                .GetValue((int)parameters[parameter.Index]);
                    }
                }

                if (right.GetType() == typeof (Null))
                {
                    if (binary.NodeType == ExpressionType.Equal)
                        return new UnaryOperator(UnaryOperatorType.IsNull, left);
                    if (binary.NodeType == ExpressionType.NotEqual)
                        return new UnaryOperator(UnaryOperatorType.IsNotNull, left);
                    throw new Exception("Operator not supported with null right member in " + binary);
                }

                return new BinaryOperator(type, left, right);
            }

            var constant = expression as ConstantExpression;
            if (constant != null)
            {
                if (constant.Type == typeof (ElmQueryable) || constant.Type.GetTypeInfo().BaseType == typeof(ElmQueryable))
                {
                    var queryable = (ElmQueryable)constant.Value;
                    Debug.Assert(queryable.ElementType == defaultType.Type);
                    var table = RegisterRootTable(); // RegisterTable(queryable.ElementType, null);
                    return new Select(ParseSelectedColumn(table), new AliasDefinition(new Table(table.Mapping.TableName, table.Mapping.TableSchema), table.Alias));
                }
                else
                {
                    return ParseParameter(constant.Value);
                }
            }

            if (expression.NodeType == ExpressionType.MemberAccess)
            {
                var memberExpression = (MemberExpression) expression;
                if (memberExpression.Expression != null && memberExpression.Expression.Type == parametersType)
                {
                    return new NamedParameter(memberExpression.Member.Name);
                }
            }

            var column = ExpressionToColumn(expression, registerTable);
            if (column != null)
            {
                return new Column(column.Table.Alias, column.Column.ColumnName);
            }

            if (expression.NodeType == ExpressionType.Call)
            {
                var call = (MethodCallExpression)expression;

                if (call.Method.DeclaringType == typeof (Queryable))
                {
                    switch (call.Method.Name)
                    {
                        case nameof(Queryable.Where):
                            return new Where(ParseExpression(call.Arguments[0], registerTable), ParseExpression(call.Arguments[1], registerTable));

                        case nameof(Queryable.Skip):
                            return new Skip(ParseExpression(call.Arguments[0], registerTable), ParseExpression(call.Arguments[1]));

                        case nameof(Queryable.Take):
                            return new Take(ParseExpression(call.Arguments[0], registerTable), ParseExpression(call.Arguments[1]));

                        case nameof(Queryable.OrderBy):
                            return new OrderBy(ParseExpression(call.Arguments[0], registerTable), ParseExpression(call.Arguments[1], registerTable));

                        case nameof(Queryable.Join):
                            return new Join(ParseExpression(call.Arguments[0], registerTable), ParseExpression(call.Arguments[1], registerTable), ParseExpression(call.Arguments[2]), ParseExpression(call.Arguments[3]), ParseExpression(call.Arguments[4]));

                        default:
                            throw new Exception("Unsupported Queryable method");
                    }
                }

                if (call.Method.DeclaringType == typeof(ExpressionHelpers))
                {
                    switch (call.Method.Name)
                    {
                        case nameof(ExpressionHelpers.Like):
                            return new BinaryOperator(BinaryOperatorType.Like, ParseExpression(call.Arguments[0], registerTable), ParseExpression(call.Arguments[1], registerTable));
                        case nameof(ExpressionHelpers.In):
                            return new BinaryOperator(BinaryOperatorType.In,
                                ParseExpression(call.Arguments[0], registerTable),
                                ParseValues((IEnumerable) Expression.Lambda(call.Arguments[1]).Compile().DynamicInvoke()));
                        case nameof(ExpressionHelpers.Between):
                            return new Between(ParseExpression(call.Arguments[0], registerTable), ParseExpression(call.Arguments[1], registerTable),
                                            ParseExpression(call.Arguments[2], registerTable));
                        default:
                            throw new Exception("Unsupported expression helper");
                    }
                }

                if (call.Method.DeclaringType == typeof(Math))
                {
                    MathFunctionType type;
                    switch (call.Method.Name)
                    {
                        case nameof(Math.Abs):
                            type = MathFunctionType.Abs;
                            break;

                        case nameof(Math.Cos):
                            type = MathFunctionType.Cos;
                            break;

                        case nameof(Math.Sin):
                            type = MathFunctionType.Sin;
                            break;
                        default:
                            throw new NotImplementedException("Not implemented math function");
                    }

                    return new MathFunction(type, ParseExpression(call.Arguments[0], registerTable));
                }

                if (call.Method.DeclaringType == typeof(SqlFunctions))
                {
                    switch (call.Method.Name)
                    {
                        case nameof(SqlFunctions.LastInsertedId):
                            return new LastInsertedId();
                        case nameof(SqlFunctions.Max):
                            return new MathFunction(MathFunctionType.Max, ParseExpression(call.Arguments[0], registerTable));
                        case nameof(SqlFunctions.Sum):
                            return new MathFunction(MathFunctionType.Sum, ParseExpression(call.Arguments[0], registerTable));
                        case nameof(SqlFunctions.IsNull):
                            return new MathFunction(MathFunctionType.IsNull, ParseExpression(call.Arguments[0], registerTable), ParseExpression(call.Arguments[1], registerTable));
                        case nameof(SqlFunctions.Case):
                            var cases = ((NewArrayExpression) call.Arguments[0]).Expressions;
                            return new Case(cases.Select(x => ParseExpression(x)));
                        case nameof(SqlFunctions.When):
                            return new When(ParseExpression(call.Arguments[0], registerTable),
                                ParseExpression(call.Arguments[1], registerTable));
                        case nameof(SqlFunctions.Else):
                            return new Else(ParseExpression(call.Arguments[0], registerTable));
                        default:
                            throw new Exception("Unsupported sql function");
                    }
                }

                if (call.Method.DeclaringType == typeof(string))
                {
                    switch (call.Method.Name)
                    {
                        case nameof(string.StartsWith):
                        {
                            var text = (string)Expression.Lambda(call.Arguments[0]).Compile().DynamicInvoke();
                            text = text.Replace("\\", "\\\\").Replace("%", "\\%") + "%";
                            return new BinaryOperator(BinaryOperatorType.Like, ParseExpression(call.Object, registerTable), ParseParameter(text));
                        }
                        case nameof(string.Contains):
                            {
                                var text = (string)Expression.Lambda(call.Arguments[0]).Compile().DynamicInvoke() ?? string.Empty;
                                text = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%") + "%";
                                return new BinaryOperator(BinaryOperatorType.Like, ParseExpression(call.Object, registerTable), ParseParameter(text));
                            }
                            
                        default:
                            throw new Exception("Unsupported string method");
                    }
                }

                if (call.Method.Name == nameof(object.Equals))
                {
                    return new BinaryOperator(BinaryOperatorType.Equal, ParseExpression(call.Object, registerTable), ParseExpression(call.Arguments[0], registerTable));
                }
            }

            var value = Expression.Lambda(expression).Compile().DynamicInvoke();
            return ParseParameter(value);
        }

        private IVisitable ParseParameter(object value)
        {
            if (value == null) return new Null();
            return new Parameter(AddParameter(value));
        }

        private IVisitable ParseValues(IEnumerable values)
        {
            var list = new List<IVisitable>();

            foreach (var value in values)
            {
                list.Add(new Parameter(AddParameter(value)));
            }
            return new Values(list);
        }
        
        //internal SelectedTable RegisterTable(TypeMapping typeMapping, string internalIdentifier)
        //{
        //    var table = tables.SingleOrDefault(t => t.InternalIdentifier == internalIdentifier);
        //    if (table == null)
        //    {
        //        table = new SelectedTable { Alias = "t" + tables.Count, InternalIdentifier = internalIdentifier, Mapping = typeMapping };
        //        tables.Add(table);
        //    }
        //    return table;
        //}

        internal SelectedTable RegisterRootTable()
        {
            if (defaultTable == null)
            {
                defaultTable = new SelectedTable { Alias = "t", Parent = null, Mapping = defaultType };
                tables.Add(defaultTable);
            }
            return defaultTable;
        }

        /// <summary>Register a table in the list of selected tables</summary>
        /// <param name="aliasExpression">An expression that points to the table</param>
        /// <returns>The selected table</returns>
        //protected internal SelectedTable RegisterTable(Expression aliasExpression)
        //{
        //    Type type;
        //    var alias = CreateTableInternalIdentifier(aliasExpression, out type);
        //    return RegisterTable(Mapper.GetTypeMapping(type), alias);
        //}

        /// <summary>Adds a column to the list of selected values</summary>
        /// <param name="column"></param>
        internal void SelectField(TableColumn column)
        {
            SelectField(column.Column, column.Table);
        }

        /// <summary>Adds a column from a given table to the list of selected values</summary>
        /// <param name="property">The property mapping</param>
        /// <param name="table">The selected table</param>
        internal SelectedField SelectField(PropertyMapping property, SelectedTable table)
        {
            var selectedField = new SelectedField { PropertyMapping = property, Table = table, Index = selectedFields.Count };
            selectedFields.Add(selectedField);
            return selectedField;
        }

        /// <summary>Select a column by an expression that maps to a column</summary>
        /// <param name="column">The expression</param>
        internal void SelectField(Expression column)
        {
            SelectField(ExpressionToColumn(column, registerTable: true));
        }

        /// <summary>Converts an expression to a column</summary>
        /// <param name="columnExpression">The expression that should point to a table</param>
        /// <param name="registerTable"><c>true</c> if the table must be added to the list of selected tables if it was not</param>
        /// <returns>The column or <c>null</c> if the expression did not point to a column</returns>
        internal TableColumn ExpressionToColumn(Expression columnExpression, bool registerTable = false)
        {
            if (columnExpression.NodeType == ExpressionType.Convert)
            {
                columnExpression = ((UnaryExpression) columnExpression).Operand;
            }

            if (columnExpression.NodeType == ExpressionType.Parameter)
            {
                return new TableColumn {Column = defaultType.Key, Table = defaultTable };
            }

            if (columnExpression.NodeType == ExpressionType.Call)
            {
                var callExpression = (MethodCallExpression)columnExpression;
                if (callExpression.Method.DeclaringType == typeof (ExpressionHelpers) &&
                    callExpression.Method.Name == nameof(ExpressionHelpers.Property))
                {
                    var propertyInfo = (PropertyInfo)Expression.Lambda(callExpression.Arguments[1]).Compile().DynamicInvoke();
                    var table = GetTable(callExpression.Arguments[0], registerTable);
                    return new TableColumn {Column = table.Mapping.Columns[propertyInfo.Name], Table = table };
                }

                if (callExpression.Method.DeclaringType == typeof(ExpressionHelpers) &&
                    callExpression.Method.Name == nameof(ExpressionHelpers.Key))
                {
                    var table = GetTable(callExpression.Arguments[0], registerTable);
                    return new TableColumn { Column = table.Mapping.Key, Table = table };
                }
                return null;
            }
            
            if (columnExpression.NodeType != ExpressionType.MemberAccess)
            {
                return null;
            }

            var columnMember = (MemberExpression)columnExpression;
            // var parentType = columnMember.Expression.Type;
            // var parentTypeMapping = Mapper.GetTypeMapping(parentType);
            var parentTable = GetTable(columnMember.Expression, registerTable);
            if (parentTable == null)
            {
                // This is not an expression that points to the column of table (should be a constant or a variable)
                // Maybe it's the table itself
                var table = GetTable(columnExpression, registerTable);
                if (table == null) return null;
                return new TableColumn
                {
                    Table = table,
                    Column = table.Mapping.Key
                };
            }

            return new TableColumn
            {
                Table = parentTable,
                Column = parentTable.Mapping.GetColumn(columnMember.Member)
            };
            /*
            var columnMemberExpression = columnMember.Expression;
            if (columnMemberExpression != null)
            {
                if (columnMemberExpression.NodeType == ExpressionType.Convert)
                    columnMemberExpression = ((UnaryExpression)columnMemberExpression).Operand;

                var memberExpression = columnMemberExpression as MemberExpression;
                if (memberExpression != null)
                {
                    // The case x => x.Foo.Bar
                    var table = registerTable ? RegisterTable(memberExpression) : GetTable(memberExpression);
                    if (table == null)
                    {
                        // Asking the id of the item pointed by a foreign key is the same as asking the foreign key
                        var mapping = Mapper.GetTypeMapping(memberExpression.Type);
                        var keyOfTable = mapping.Key;
                        if (keyOfTable != null && AreSameProperties(columnMember.Member, keyOfTable.PropertyInfo))
                        {
                            return ExpressionToColumn(memberExpression);
                        }
                        return null;
                    }

                    return new TableColumn { Column = table.Mapping.Columns[columnMember.Member.Name], Table = table };
                }

                var parameterExpression = columnMemberExpression as ParameterExpression;
                if (parameterExpression != null && parameterExpression.Type == defaultType.Type)
                {
                    // The case x => x.Foo 
                    if (defaultTable == null)
                    {
                        if (registerTable)
                        {
                            defaultTable = RegisterRootTable();
                        }
                        else
                        {
                            // TODO Should be an error
                            var table = GetTable(columnExpression);
                            if (table != null)
                            {
                                // TODO Should not be reachable
                                return new TableColumn { Column = table.Mapping.Key, Table = table };
                            }
                            return null;
                        }
                    }
                    return new TableColumn { Column = defaultTable.Mapping.Columns[columnMember.Member.Name], Table = defaultTable };
                }
            }
            
            // If x => x.Foo where Foo is a table, selects its primary key
            var columnAsTable = GetTable(columnExpression);
            if (columnAsTable != null)
            {
                return new TableColumn {Column = columnAsTable.Mapping.Key, Table = columnAsTable};
            }
            return null;*/
        }

        private bool AreSameProperties(MemberInfo member, PropertyInfo propertyInfo)
        {
            return member.Module.Name == propertyInfo.Module.Name && member.Name == propertyInfo.Name &&
                   member.DeclaringType == propertyInfo.DeclaringType;
        }

        /// <summary>
        /// Get the key column from a table expression.
        /// Example: x => x.Identity will returns the Id column from the Identity table
        /// </summary>
        /// <param name="tableExpression">The expression</param>
        /// <returns></returns>
        internal TableColumn GetTableKey(Expression tableExpression)
        {
            var table = GetTable(tableExpression, false);
            return new TableColumn {Column = table.Mapping.Key, Table = table};
        }

        private IVisitable ParseSelectedColumn(SelectedTable table)
        {
            var columns = table.Mapping.Columns;
            var fields = new List<IVisitable>();
            AddAllColumns(table, columns, fields, null);
            return new Fields(fields);
        }

        private void AddAllColumns(SelectedTable table, Dictionary<string, PropertyMapping> columns, List<IVisitable> fields, string baseName)
        {
            foreach (var column in columns.Values)
            {
                if (column.ComplexType != null)
                {
                    AddAllColumns(table, column.ComplexType.Columns, fields, column.ComposeName(baseName));
                    continue;
                }
                SelectField(column, table);
                fields.Add(new Column(table.Alias, column.ColumnName));
            }
        }

        public class TableColumn
        {
            public SelectedTable Table { get; set; }

            public PropertyMapping Column { get; set; }
        }

        public SqlStringBuilder StringBuilder => query;
    }
}