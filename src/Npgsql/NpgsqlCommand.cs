// created on 21/5/2002 at 20:03

// Npgsql.NpgsqlCommand.cs
//
// Author:
//    Francisco Jr. (fxjrlists@yahoo.com.br)
//
//    Copyright (C) 2002 The Npgsql Development Team
//    npgsql-general@gborg.postgresql.org
//    http://gborg.postgresql.org/project/npgsql/projdisplay.php
//
// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
//
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using NpgsqlTypes;

#if WITHDESIGN

#endif

namespace Npgsql
{
    /// <summary>
    /// Represents a SQL statement or function (stored procedure) to execute
    /// against a PostgreSQL database. This class cannot be inherited.
    /// </summary>
#if WITHDESIGN
    [System.Drawing.ToolboxBitmapAttribute(typeof(NpgsqlCommand)), ToolboxItem(true)]
#endif

    public sealed class NpgsqlCommand : DbCommand, ICloneable
    {
        private enum PrepareStatus
        {
            NotPrepared,
            NeedsPrepare,
            V2Prepared,
            V3Prepared
        }

        // Logging related values
        private static readonly String CLASSNAME = MethodBase.GetCurrentMethod().DeclaringType.Name;
        private static readonly ResourceManager resman = new ResourceManager(MethodBase.GetCurrentMethod().DeclaringType);

        private NpgsqlConnection connection;
        private NpgsqlConnector m_Connector; //renamed to account for hiding it in a local function
        //if all locals were named with this prefix, it would solve LOTS of issues.
        private NpgsqlTransaction transaction;
        private String commandText;
        private Int32 timeout;
        private CommandType commandType;
        private readonly NpgsqlParameterCollection parameters = new NpgsqlParameterCollection();
        private String planName;
        private Boolean designTimeVisible;

        private PrepareStatus prepared = PrepareStatus.NotPrepared;
        private NpgsqlBind bind = null;
        private NpgsqlExecute execute = null;
        private bool portalDescribeSent = false;
        private NpgsqlRowDescription currentRowDescription = null;

        private Int64 lastInsertedOID = 0;

        // locals about function support so we don`t need to check it everytime a function is called.
        private Boolean functionChecksDone = false;
        private Boolean functionNeedsColumnListDefinition = false; // Functions don't return record by default.

        private Boolean commandTimeoutSet = false;

        private UpdateRowSource updateRowSource = UpdateRowSource.Both;

        private static readonly Array ParamNameCharTable;

        // Constructors
        static NpgsqlCommand()
        {
            ParamNameCharTable = BuildParameterNameCharacterTable();
        }

        private static Array BuildParameterNameCharacterTable()
        {
            Array paramNameCharTable;

            // Table has lower bound of (int)'.';
            paramNameCharTable = Array.CreateInstance(typeof(byte), new int[] {'z' - '.' + 1}, new int[] {'.'});

            paramNameCharTable.SetValue((byte)'.', (int)'.');

            for (int i = '0' ; i <= '9' ; i++)
            {
                paramNameCharTable.SetValue((byte)i, i);
            }

            for (int i = 'A' ; i <= 'Z' ; i++)
            {
                paramNameCharTable.SetValue((byte)i, i);
            }

            paramNameCharTable.SetValue((byte)'_', (int)'_');

            for (int i = 'a' ; i <= 'z' ; i++)
            {
                paramNameCharTable.SetValue((byte)i, i);
            }

            return paramNameCharTable;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Npgsql.NpgsqlCommand">NpgsqlCommand</see> class.
        /// </summary>
        public NpgsqlCommand()
            : this(String.Empty, null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Npgsql.NpgsqlCommand">NpgsqlCommand</see> class with the text of the query.
        /// </summary>
        /// <param name="cmdText">The text of the query.</param>
        public NpgsqlCommand(String cmdText)
            : this(cmdText, null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Npgsql.NpgsqlCommand">NpgsqlCommand</see> class with the text of the query and a <see cref="Npgsql.NpgsqlConnection">NpgsqlConnection</see>.
        /// </summary>
        /// <param name="cmdText">The text of the query.</param>
        /// <param name="connection">A <see cref="Npgsql.NpgsqlConnection">NpgsqlConnection</see> that represents the connection to a PostgreSQL server.</param>
        public NpgsqlCommand(String cmdText, NpgsqlConnection connection)
            : this(cmdText, connection, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Npgsql.NpgsqlCommand">NpgsqlCommand</see> class with the text of the query, a <see cref="Npgsql.NpgsqlConnection">NpgsqlConnection</see>, and the <see cref="Npgsql.NpgsqlTransaction">NpgsqlTransaction</see>.
        /// </summary>
        /// <param name="cmdText">The text of the query.</param>
        /// <param name="connection">A <see cref="Npgsql.NpgsqlConnection">NpgsqlConnection</see> that represents the connection to a PostgreSQL server.</param>
        /// <param name="transaction">The <see cref="Npgsql.NpgsqlTransaction">NpgsqlTransaction</see> in which the <see cref="Npgsql.NpgsqlCommand">NpgsqlCommand</see> executes.</param>
        public NpgsqlCommand(String cmdText, NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, CLASSNAME);

            planName = String.Empty;
            commandText = cmdText;
            this.connection = connection;

            if (this.connection != null)
            {
                this.m_Connector = connection.Connector;

                if (m_Connector != null && m_Connector.AlwaysPrepare)
                {
                    prepared = PrepareStatus.NeedsPrepare;
                }
            }

            commandType = CommandType.Text;
            this.Transaction = transaction;

            SetCommandTimeout();
        }

        /// <summary>
        /// Used to execute internal commands.
        /// </summary>
        internal NpgsqlCommand(String cmdText, NpgsqlConnector connector)
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, CLASSNAME);

            planName = String.Empty;
            commandText = cmdText;
            this.m_Connector = connector;
            commandType = CommandType.Text;

            // Removed this setting. It was causing too much problem.
            // Do internal commands really need different timeout setting?
            // Internal commands aren't affected by command timeout value provided by user.
            // timeout = 20;
        }

        // Public properties.
        /// <summary>
        /// Gets or sets the SQL statement or function (stored procedure) to execute at the data source.
        /// </summary>
        /// <value>The Transact-SQL statement or stored procedure to execute. The default is an empty string.</value>
        [Category("Data"), DefaultValue("")]
        public override String CommandText
        {
            get { return commandText; }

            set
            {
                // [TODO] Validate commandtext.
                NpgsqlEventLog.LogPropertySet(LogLevel.Debug, CLASSNAME, "CommandText", value);
                commandText = value;

                UnPrepare();

                functionChecksDone = false;
            }
        }

        /// <summary>
        /// Gets or sets the wait time before terminating the attempt
        /// to execute a command and generating an error.
        /// </summary>
        /// <value>The time (in seconds) to wait for the command to execute.
        /// The default is 20 seconds.</value>
        [DefaultValue(20)]
        public override Int32 CommandTimeout
        {
            get { return timeout; }

            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(resman.GetString("Exception_CommandTimeoutLessZero"));
                }

                timeout = value;
                NpgsqlEventLog.LogPropertySet(LogLevel.Debug, CLASSNAME, "CommandTimeout", value);

                commandTimeoutSet = true;

            }
        }

        /// <summary>
        /// Gets or sets a value indicating how the
        /// <see cref="Npgsql.NpgsqlCommand.CommandText">CommandText</see> property is to be interpreted.
        /// </summary>
        /// <value>One of the <see cref="System.Data.CommandType">CommandType</see> values. The default is <see cref="System.Data.CommandType">CommandType.Text</see>.</value>
        [Category("Data"), DefaultValue(CommandType.Text)]
        public override CommandType CommandType
        {
            get { return commandType; }

            set
            {
                commandType = value;
                NpgsqlEventLog.LogPropertySet(LogLevel.Debug, CLASSNAME, "CommandType", value);
            }
        }

        protected override DbConnection DbConnection
        {
            get { return Connection; }

            set
            {
                Connection = (NpgsqlConnection)value;
                NpgsqlEventLog.LogPropertySet(LogLevel.Debug, CLASSNAME, "DbConnection", value);
            }
        }

        /// <summary>
        /// Gets or sets the <see cref="Npgsql.NpgsqlConnection">NpgsqlConnection</see>
        /// used by this instance of the <see cref="Npgsql.NpgsqlCommand">NpgsqlCommand</see>.
        /// </summary>
        /// <value>The connection to a data source. The default value is a null reference.</value>
        [Category("Behavior"), DefaultValue(null)]
        public new NpgsqlConnection Connection
        {
            get
            {
                NpgsqlEventLog.LogPropertyGet(LogLevel.Debug, CLASSNAME, "Connection");
                return connection;
            }

            set
            {
                if (this.Connection == value)
                {
                    return;
                }

                //if (this.transaction != null && this.transaction.Connection == null)
                //  this.transaction = null;

                // All this checking needs revising. It should be simpler.
                // This this.Connector != null check was added to remove the nullreferenceexception in case
                // of the previous connection has been closed which makes Connector null and so the last check would fail.
                // See bug 1000581 for more details.
                if (this.transaction != null && this.connection != null && this.Connector != null && this.Connector.Transaction != null)
                {
                    throw new InvalidOperationException(resman.GetString("Exception_SetConnectionInTransaction"));
                }

                this.connection = value;
                Transaction = null;
                if (this.connection != null)
                {
                    m_Connector = this.connection.Connector;
                }

                SetCommandTimeout();

                NpgsqlEventLog.LogPropertySet(LogLevel.Debug, CLASSNAME, "Connection", value);
            }
        }

        internal NpgsqlConnector Connector
        {
            get
            {
                if (this.connection != null)
                {
                    m_Connector = this.connection.Connector;
                }

                return m_Connector;
            }
        }

        internal Type[] ExpectedTypes { get; set; }

        protected override DbParameterCollection DbParameterCollection
        {
            get { return Parameters; }
        }

        /// <summary>
        /// Gets the <see cref="Npgsql.NpgsqlParameterCollection">NpgsqlParameterCollection</see>.
        /// </summary>
        /// <value>The parameters of the SQL statement or function (stored procedure). The default is an empty collection.</value>
#if WITHDESIGN
        [Category("Data"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
#endif

        public new NpgsqlParameterCollection Parameters
        {
            get
            {
                NpgsqlEventLog.LogPropertyGet(LogLevel.Debug, CLASSNAME, "Parameters");
                return parameters;
            }
        }

        protected override DbTransaction DbTransaction
        {
            get { return Transaction; }
            set
            {
                Transaction = (NpgsqlTransaction)value;
                NpgsqlEventLog.LogPropertySet(LogLevel.Debug, CLASSNAME, "IDbCommand.Transaction", value);
            }
        }

        /// <summary>
        /// Gets or sets the <see cref="Npgsql.NpgsqlTransaction">NpgsqlTransaction</see>
        /// within which the <see cref="Npgsql.NpgsqlCommand">NpgsqlCommand</see> executes.
        /// </summary>
        /// <value>The <see cref="Npgsql.NpgsqlTransaction">NpgsqlTransaction</see>.
        /// The default value is a null reference.</value>
#if WITHDESIGN
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
#endif

        public new NpgsqlTransaction Transaction
        {
            get
            {
                NpgsqlEventLog.LogPropertyGet(LogLevel.Debug, CLASSNAME, "Transaction");

                if (this.transaction != null && this.transaction.Connection == null)
                {
                    this.transaction = null;
                }
                return this.transaction;
            }

            set
            {
                NpgsqlEventLog.LogPropertySet(LogLevel.Debug, CLASSNAME, "Transaction", value);

                this.transaction = value;
            }
        }

        /// <summary>
        /// Gets or sets how command results are applied to the <see cref="System.Data.DataRow">DataRow</see>
        /// when used by the <see cref="System.Data.Common.DbDataAdapter.Update(DataSet)">Update</see>
        /// method of the <see cref="System.Data.Common.DbDataAdapter">DbDataAdapter</see>.
        /// </summary>
        /// <value>One of the <see cref="System.Data.UpdateRowSource">UpdateRowSource</see> values.</value>
#if WITHDESIGN
        [Category("Behavior"), DefaultValue(UpdateRowSource.Both)]
#endif

        public override UpdateRowSource UpdatedRowSource
        {
            get
            {
                NpgsqlEventLog.LogPropertyGet(LogLevel.Debug, CLASSNAME, "UpdatedRowSource");

                return updateRowSource;
            }
            set
            {
                switch (value)
                {
                        // validate value (required based on base type contract)
                    case UpdateRowSource.None:
                    case UpdateRowSource.OutputParameters:
                    case UpdateRowSource.FirstReturnedRecord:
                    case UpdateRowSource.Both:
                        updateRowSource = value;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        /// <summary>
        /// Returns oid of inserted row. This is only updated when using executenonQuery and when command inserts just a single row. If table is created without oids, this will always be 0.
        /// </summary>
        public Int64 LastInsertedOID
        {
            get { return lastInsertedOID; }
        }

        /// <summary>
        /// Attempts to cancel the execution of a <see cref="Npgsql.NpgsqlCommand">NpgsqlCommand</see>.
        /// </summary>
        /// <remarks>This Method isn't implemented yet.</remarks>
        public override void Cancel()
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "Cancel");

            try
            {
                // get copy for thread safety of null test
                NpgsqlConnector connector = Connector;
                if (connector != null)
                {
                    connector.CancelRequest();
                }
            }
            catch (IOException)
            {
                Connection.ClearPool();
            }
            catch (NpgsqlException)
            {
                // Cancel documentation says the Cancel doesn't throw on failure
            }
        }

        /// <summary>
        /// Create a new command based on this one.
        /// </summary>
        /// <returns>A new NpgsqlCommand object.</returns>
        Object ICloneable.Clone()
        {
            return Clone();
        }

        /// <summary>
        /// Create a new command based on this one.
        /// </summary>
        /// <returns>A new NpgsqlCommand object.</returns>
        public NpgsqlCommand Clone()
        {
            // TODO: Add consistency checks.

            NpgsqlCommand clone = new NpgsqlCommand(CommandText, Connection, Transaction);
            clone.CommandTimeout = CommandTimeout;
            clone.CommandType = CommandType;
            clone.DesignTimeVisible = DesignTimeVisible;
            if (ExpectedTypes != null)
            {
                clone.ExpectedTypes = (Type[])ExpectedTypes.Clone();
            }
            foreach (NpgsqlParameter parameter in Parameters)
            {
                clone.Parameters.Add(parameter.Clone());
            }
            return clone;
        }

        /// <summary>
        /// Creates a new instance of an <see cref="System.Data.Common.DbParameter">DbParameter</see> object.
        /// </summary>
        /// <returns>An <see cref="System.Data.Common.DbParameter">DbParameter</see> object.</returns>
        protected override DbParameter CreateDbParameter()
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "CreateDbParameter");

            return CreateParameter();
        }

        /// <summary>
        /// Creates a new instance of a <see cref="Npgsql.NpgsqlParameter">NpgsqlParameter</see> object.
        /// </summary>
        /// <returns>A <see cref="Npgsql.NpgsqlParameter">NpgsqlParameter</see> object.</returns>
        public new NpgsqlParameter CreateParameter()
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "CreateParameter");

            return new NpgsqlParameter();
        }

        /// <summary>
        /// Slightly optimised version of ExecuteNonQuery() for internal ues in cases where the number
        /// of affected rows is of no interest.
        /// This function must not be called with a query that returns result rows, or after calling Prepare().
        /// </summary>
        internal void ExecuteBlind()
        {
            if (prepared == PrepareStatus.V2Prepared || prepared == PrepareStatus.V3Prepared)
            {
                throw new InvalidOperationException("Cannot call ExecuteBlind() on a prepared command");
            }

            NpgsqlQuery query;

            query = new NpgsqlQuery(m_Connector, GetCommandText());

            // Block the notification thread before writing anything to the wire.
            using (var blocker = m_Connector.BlockNotificationThread())
            {
                // Write the Query message to the wire.
                m_Connector.Query(query);

                // Flush, and wait for and discard all responses.
                m_Connector.ProcessAndDiscardBackendResponses();
            }
        }

        /// <summary>
        /// Executes a SQL statement against the connection and returns the number of rows affected.
        /// </summary>
        /// <returns>The number of rows affected if known; -1 otherwise.</returns>
        public override Int32 ExecuteNonQuery()
        {
            //We treat this as a simple wrapper for calling ExecuteReader() and then
            //update the records affected count at every call to NextResult();
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "ExecuteNonQuery");
            int? ret = null;
            using (NpgsqlDataReader rdr = GetReader(CommandBehavior.SequentialAccess))
            {
                do
                {
                    int thisRecord = rdr.RecordsAffected;
                    if (thisRecord != -1)
                    {
                        ret = (ret ?? 0) + thisRecord;
                    }
                    lastInsertedOID = rdr.LastInsertedOID ?? lastInsertedOID;
                }
                while (rdr.NextResult());
            }
            return ret ?? -1;
        }

        /// <summary>
        /// Sends the <see cref="Npgsql.NpgsqlCommand.CommandText">CommandText</see> to
        /// the <see cref="Npgsql.NpgsqlConnection">Connection</see> and builds a
        /// <see cref="Npgsql.NpgsqlDataReader">NpgsqlDataReader</see>
        /// using one of the <see cref="System.Data.CommandBehavior">CommandBehavior</see> values.
        /// </summary>
        /// <param name="behavior">One of the <see cref="System.Data.CommandBehavior">CommandBehavior</see> values.</param>
        /// <returns>A <see cref="Npgsql.NpgsqlDataReader">NpgsqlDataReader</see> object.</returns>
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            return ExecuteReader(behavior);
        }

        /// <summary>
        /// Sends the <see cref="Npgsql.NpgsqlCommand.CommandText">CommandText</see> to
        /// the <see cref="Npgsql.NpgsqlConnection">Connection</see> and builds a
        /// <see cref="Npgsql.NpgsqlDataReader">NpgsqlDataReader</see>.
        /// </summary>
        /// <returns>A <see cref="Npgsql.NpgsqlDataReader">NpgsqlDataReader</see> object.</returns>
        public new NpgsqlDataReader ExecuteReader()
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "ExecuteReader");

            return ExecuteReader(CommandBehavior.Default);
        }

        /// <summary>
        /// Sends the <see cref="Npgsql.NpgsqlCommand.CommandText">CommandText</see> to
        /// the <see cref="Npgsql.NpgsqlConnection">Connection</see> and builds a
        /// <see cref="Npgsql.NpgsqlDataReader">NpgsqlDataReader</see>
        /// using one of the <see cref="System.Data.CommandBehavior">CommandBehavior</see> values.
        /// </summary>
        /// <param name="cb">One of the <see cref="System.Data.CommandBehavior">CommandBehavior</see> values.</param>
        /// <returns>A <see cref="Npgsql.NpgsqlDataReader">NpgsqlDataReader</see> object.</returns>
        /// <remarks>Currently the CommandBehavior parameter is ignored.</remarks>
        public new NpgsqlDataReader ExecuteReader(CommandBehavior cb)
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "ExecuteReader", cb);

            // Close connection if requested even when there is an error.

            try
            {
                if (connection != null)
                {
                    if (connection.PreloadReader)
                    {
                        //Adjust behaviour so source reader is sequential access - for speed - and doesn't close the connection - or it'll do so at the wrong time.
                        CommandBehavior adjusted = (cb | CommandBehavior.SequentialAccess) & ~CommandBehavior.CloseConnection;

                        return new CachingDataReader(GetReader(adjusted), cb);
                    }
                }

                return GetReader(cb);
            }
            catch (Exception)
            {
                if ((cb & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection)
                {
                    connection.Close();
                }

                throw;
            }
        }

        internal ForwardsOnlyDataReader GetReader(CommandBehavior cb)
        {
            CheckConnectionState();

            // reset any responses just before getting new ones
            Connector.Mediator.ResetResponses();

            // Set command timeout.
            m_Connector.Mediator.CommandTimeout = CommandTimeout;

            // Block the notification thread before writing anything to the wire.
            using (m_Connector.BlockNotificationThread())
            {
                IEnumerable<IServerResponseObject> responseEnum;
                ForwardsOnlyDataReader reader;

                if (prepared == PrepareStatus.NeedsPrepare)
                {
                    PrepareInternal();
                }

                if (prepared == PrepareStatus.NotPrepared || prepared == PrepareStatus.V2Prepared)
                {
                    NpgsqlQuery query;

                    query = new NpgsqlQuery(m_Connector, GetCommandText());

                    // Write the Query message to the wire.
                    m_Connector.Query(query);

                    // Flush and wait for responses.
                    responseEnum = m_Connector.ProcessBackendResponsesEnum();

                    // Construct the return reader.
                    reader = new ForwardsOnlyDataReader(
                        responseEnum,
                        cb,
                        this,
                        m_Connector.BlockNotificationThread()
                    );

                    if (
                        commandType == CommandType.StoredProcedure
                        && reader.FieldCount == 1
                        && reader.GetDataTypeName(0) == "refcursor"
                    )
                    {
                        // When a function returns a sole column of refcursor, transparently
                        // FETCH ALL from every such cursor and return those results.
                        StringWriter sw = new StringWriter();
                        string queryText;

                        while (reader.Read())
                        {
                            sw.WriteLine("FETCH ALL FROM \"{0}\";", reader.GetString(0));
                        }

                        reader.Dispose();

                        queryText = sw.ToString();

                        if (queryText == "")
                        {
                            queryText = ";";
                        }

                        // Passthrough the commandtimeout to the inner command, so user can also control its timeout.
                        // TODO: Check if there is a better way to handle that.

                        query = new NpgsqlQuery(m_Connector, queryText);

                        // Write the Query message to the wire.
                        m_Connector.Query(query);

                        // Flush and wait for responses.
                        responseEnum = m_Connector.ProcessBackendResponsesEnum();

                        // Construct the return reader.
                        reader = new ForwardsOnlyDataReader(
                            responseEnum,
                            cb,
                            this,
                            m_Connector.BlockNotificationThread()
                        );
                    }
                }
                else
                {
                    bool sendPortalDescribe = ! portalDescribeSent;

                    // Update the Bind object with current parameter data as needed.
                    BindParameters();

                    // Write the Bind message to the wire.
                    m_Connector.Bind(bind);

                    if (sendPortalDescribe)
                    {
                        NpgsqlDescribe portalDescribe = new NpgsqlDescribePortal(bind.PortalName);

                        // Write a Describe message to the wire.
                        m_Connector.Describe(portalDescribe);

                        portalDescribeSent = true;
                    }

                    // Finally, write the Execute and Sync messages to the wire.
                    m_Connector.Execute(execute);
                    m_Connector.Sync();

                    // Flush and wait for responses.
                    responseEnum = m_Connector.ProcessBackendResponsesEnum();

                    // Construct the return reader, possibly with a saved row description.
                    reader = new ForwardsOnlyDataReader(
                        responseEnum,
                        cb,
                        this,
                        m_Connector.BlockNotificationThread(),
                        true,
                        currentRowDescription
                    );

                    if (sendPortalDescribe)
                    {
                        // We sent a Describe message. If the query produces a result set,
                        // PG sent a row description, and the reader has now found it,
                        currentRowDescription = reader.CurrentDescription;
                    }
                }

                return reader;
            }
        }

        ///<summary>
        /// This method binds the parameters from parameters collection to the bind
        /// message.
        /// </summary>
        private void BindParameters()
        {
            if (parameters.Count != 0)
            {
                byte[][] parameterValues = bind.ParameterValues;
                Int16[] parameterFormatCodes = bind.ParameterFormatCodes;
                bool bindAll = false;
                bool bound = false;

                if (parameterValues == null || parameterValues.Length != parameters.Count)
                {
                    parameterValues = new byte[parameters.Count][];
                    bindAll = true;
                }

                for (Int32 i = 0; i < parameters.Count; i++)
                {
                    if (! bindAll && parameters[i].Bound)
                    {
                        continue;
                    }

                    parameterValues[i] = parameters[i].TypeInfo.ConvertToBackend(parameters[i].Value, true, Connector.NativeToBackendTypeConverterOptions);

                    bound = true;
                    parameters[i].Bound = true;

                    if (parameterValues[i] == null) {
                        parameterFormatCodes[i]= (Int16)FormatCode.Binary;
                    } else {
                        parameterFormatCodes[i] = parameters[i].TypeInfo.SupportsBinaryBackendData ? (Int16)FormatCode.Binary : (Int16)FormatCode.Text;
                    }
                }

                if (bound)
                {
                    bind.ParameterValues = parameterValues;
                    bind.ParameterFormatCodes = parameterFormatCodes;
                }
            }
        }

        /// <summary>
        /// Executes the query, and returns the first column of the first row
        /// in the result set returned by the query. Extra columns or rows are ignored.
        /// </summary>
        /// <returns>The first column of the first row in the result set,
        /// or a null reference if the result set is empty.</returns>
        public override Object ExecuteScalar()
        {
            using (
                NpgsqlDataReader reader =
                    GetReader(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult | CommandBehavior.SingleRow))
            {
                return reader.Read() && reader.FieldCount != 0 ? reader.GetValue(0) : null;
            }
        }

        private void UnPrepare()
        {
            if (prepared == PrepareStatus.V3Prepared)
            {
                bind = null;
                execute = null;
                portalDescribeSent = false;
                currentRowDescription = null;
                prepared = PrepareStatus.NeedsPrepare;
            }
            else if (prepared == PrepareStatus.V2Prepared)
            {
                planName = String.Empty;
                prepared = PrepareStatus.NeedsPrepare;
            }
        }

        /// <summary>
        /// Creates a prepared version of the command on a PostgreSQL server.
        /// </summary>
        public override void Prepare()
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "Prepare");

            // Check the connection state.
            CheckConnectionState();

            if (! m_Connector.SupportsPrepare)
            {
                return; // Do nothing.
            }

            UnPrepare();

            // reset any responses just before getting new ones
            Connector.Mediator.ResetResponses();

            // Set command timeout.
            m_Connector.Mediator.CommandTimeout = CommandTimeout;

            PrepareInternal();
        }

        private void PrepareInternal()
        {
            if (m_Connector.BackendProtocolVersion == ProtocolVersion.Version2)
            {
                planName = Connector.NextPlanName();

                // BackendEncoding.UTF8Encoding.GetString() is temporary.  A new optimization for
                // ExecuteBlind() will negate the need.
                using (NpgsqlCommand command = new NpgsqlCommand(BackendEncoding.UTF8Encoding.GetString(GetCommandText(true, false)), m_Connector))
                {
                    command.ExecuteBlind();
                    prepared = PrepareStatus.V2Prepared;
                }
            }
            else
            {
                // Use the extended query parsing...
                planName = m_Connector.NextPlanName();
                String portalName = "";
                NpgsqlParse parse = new NpgsqlParse(planName,  GetCommandText(true, true), new Int32[] { });
                NpgsqlDescribe statementDescribe = new NpgsqlDescribeStatement(planName);
                IEnumerable<IServerResponseObject> responseEnum;
                NpgsqlRowDescription returnRowDesc = null;

                // Write Parse, Describe, and Sync messages to the wire.
                m_Connector.Parse(parse);
                m_Connector.Describe(statementDescribe);
                m_Connector.Sync();

                // Flush and wait for response.
                responseEnum = m_Connector.ProcessBackendResponsesEnum();

                // Look for a NpgsqlRowDescription in the responses, discarding everything else.
                foreach (IServerResponseObject response in responseEnum)
                {
                    if (response is NpgsqlRowDescription)
                    {
                        returnRowDesc = (NpgsqlRowDescription)response;
                    }
                    else if (response is IDisposable)
                    {
                        (response as IDisposable).Dispose();
                    }
                }

                Int16[] resultFormatCodes;

                if (returnRowDesc != null)
                {
                    resultFormatCodes = new Int16[returnRowDesc.NumFields];

                    for (int i = 0; i < returnRowDesc.NumFields; i++)
                    {
                        NpgsqlRowDescription.FieldData returnRowDescData = returnRowDesc[i];

                        if (returnRowDescData.TypeInfo != null)
                        {
                            // Binary format?
                            resultFormatCodes[i] = returnRowDescData.TypeInfo.SupportsBinaryBackendData ? (Int16)FormatCode.Binary : (Int16)FormatCode.Text;
                        }
                        else
                        {
                            // Text Format
                            resultFormatCodes[i] = (Int16)FormatCode.Text;
                        }
                    }
                }
                else
                {
                    resultFormatCodes = new Int16[] { 0 };
                }

                // The Bind and Execute message objects live through multiple Executes.
                // Only Bind changes at all between Executes, which is done in BindParameters().
                bind = new NpgsqlBind(portalName, planName, new Int16[Parameters.Count], null, resultFormatCodes);
                execute = new NpgsqlExecute(portalName, 0);
                prepared = PrepareStatus.V3Prepared;
            }
        }

        /*
        /// <summary>
        /// Releases the resources used by the <see cref="Npgsql.NpgsqlCommand">NpgsqlCommand</see>.
        /// </summary>
        protected override void Dispose (bool disposing)
        {

            if (disposing)
            {
                // Only if explicitly calling Close or dispose we still have access to
                // managed resources.
                NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "Dispose");
                if (connection != null)
                {
                    connection.Dispose();
                }
                base.Dispose(disposing);

            }
        }*/

        ///<summary>
        /// This method checks the connection state to see if the connection
        /// is set or it is open. If one of this conditions is not met, throws
        /// an InvalidOperationException
        ///</summary>
        private void CheckConnectionState()
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "CheckConnectionState");

            // Check the connection state.
            if (Connector == null || Connector.State == ConnectionState.Closed)
            {
                throw new InvalidOperationException(resman.GetString("Exception_ConnectionNotOpen"));
            }
            if (Connector.State != ConnectionState.Open)
            {
                throw new InvalidOperationException(
                    "There is already an open DataReader associated with this Command which must be closed first.");
            }
        }

        /// <summary>
        /// This method substitutes the <see cref="Npgsql.NpgsqlCommand.Parameters">Parameters</see>, if exist, in the command
        /// to their actual values.
        /// The parameter name format is <b>:ParameterName</b>.
        /// </summary>
        /// <returns>A version of <see cref="Npgsql.NpgsqlCommand.CommandText">CommandText</see> with the <see cref="Npgsql.NpgsqlCommand.Parameters">Parameters</see> inserted.</returns>
        internal byte[] GetCommandText()
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "GetCommandText");

            byte[] ret = string.IsNullOrEmpty(planName) ? GetCommandText(false, false) : GetExecuteCommandText();
            // In constructing the command text, we potentially called internal
            // queries.  Reset command timeout and SQL sent.
            m_Connector.Mediator.ResetResponses();
            m_Connector.Mediator.CommandTimeout = CommandTimeout;

            return ret;
        }

        private Boolean CheckFunctionNeedsColumnDefinitionList()
        {
            // If and only if a function returns "record" and has no OUT ("o" in proargmodes), INOUT ("b"), or TABLE
            // ("t") return arguments to characterize the result columns, we must provide a column definition list.
            // See http://pgfoundry.org/forum/forum.php?thread_id=1075&forum_id=519
            // We would use our Output and InputOutput parameters to construct that column definition list.  If we have
            // no such parameters, skip the check: we could only construct "AS ()", which yields a syntax error.

            // Updated after 0.99.3 to support the optional existence of a name qualifying schema and allow for case insensitivity
            // when the schema or procedure name do not contain a quote.
            // The hard-coded schema name 'public' was replaced with code that uses schema as a qualifier, only if it is provided.

            String returnRecordQuery;

            StringBuilder parameterTypes = new StringBuilder("");

            // Process parameters

            Boolean seenDef = false;
            foreach (NpgsqlParameter p in Parameters)
            {
                if ((p.Direction == ParameterDirection.Input) || (p.Direction == ParameterDirection.InputOutput))
                {
                    parameterTypes.Append(Connection.Connector.OidToNameMapping[p.TypeInfo.Name].OID.ToString() + " ");
                }

                if ((p.Direction == ParameterDirection.Output) || (p.Direction == ParameterDirection.InputOutput))
                {
                    seenDef = true;
                }
            }

            if (!seenDef)
            {
                return false;
            }

            // Process schema name.

            String schemaName = String.Empty;
            String procedureName = String.Empty;

            String[] fullName = CommandText.Split('.');

            String predicate = "prorettype = ( select oid from pg_type where typname = 'record' ) "
                + "and proargtypes=:proargtypes and proname=:proname "
                // proargmodes && array['o','b','t']::"char"[] performs just as well, but it requires PostgreSQL 8.2.
                + "and ('o' = any (proargmodes) OR 'b' = any (proargmodes) OR 't' = any (proargmodes)) is not true";
            if (fullName.Length == 2)
            {
                returnRecordQuery =
                "select count(*) > 0 from pg_proc p left join pg_namespace n on p.pronamespace = n.oid where " + predicate + " and n.nspname=:nspname";

                schemaName = (fullName[0].IndexOf("\"") != -1) ? fullName[0] : fullName[0].ToLower();
                procedureName = (fullName[1].IndexOf("\"") != -1) ? fullName[1] : fullName[1].ToLower();
            }
            else
            {
                // Instead of defaulting don't use the nspname, as an alternative, query pg_proc and pg_namespace to try and determine the nspname.
                //schemaName = "public"; // This was removed after build 0.99.3 because the assumption that a function is in public is often incorrect.
                returnRecordQuery =
                    "select count(*) > 0 from pg_proc p where " + predicate;

                procedureName = (CommandText.IndexOf("\"") != -1) ? CommandText : CommandText.ToLower();
            }

            bool ret;

            using (NpgsqlCommand c = new NpgsqlCommand(returnRecordQuery, Connection))
            {
                c.Parameters.Add(new NpgsqlParameter("proargtypes", NpgsqlDbType.Oidvector));
                c.Parameters.Add(new NpgsqlParameter("proname", NpgsqlDbType.Name));

                c.Parameters[0].Value = parameterTypes.ToString();
                c.Parameters[1].Value = procedureName;

                if (schemaName != null && schemaName.Length > 0)
                {
                    c.Parameters.Add(new NpgsqlParameter("nspname", NpgsqlDbType.Name));
                    c.Parameters[2].Value = schemaName;
                }

                ret = (Boolean)c.ExecuteScalar();
            }

            // reset any responses just before getting new ones
            m_Connector.Mediator.ResetResponses();

            // Set command timeout.
            m_Connector.Mediator.CommandTimeout = CommandTimeout;

            return ret;
        }

        private void AddFunctionColumnListSupport(Stream st)
        {
            PGUtil.WriteString(st, " AS (");

            for (int i = 0 ; i < Parameters.Count ; i++)
            {
                var p = Parameters[i];

                switch(p.Direction)
                {
                    case ParameterDirection.Output: case ParameterDirection.InputOutput:
                        if (i > 0)
                        {
                            st.WriteString(", ");
                        }

                        st
                            .WriteString(p.CleanName)
                            .WriteBytes((byte)ASCIIBytes.Space)
                            .WriteString(p.TypeInfo.Name);

                        break;
                }
            }

            st.WriteByte((byte)ASCIIBytes.ParenRight);
        }

        private class StringChunk
        {
            public int Begin;
            public int Length;

            public StringChunk(int begin, int length)
            {
                this.Begin = begin;
                this.Length = length;
            }
        }

        private byte[] GetCommandText(bool prepare, bool forExtendQuery)
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "GetCommandText");

            MemoryStream commandBuilder = new MemoryStream();
            StringChunk[] chunks;

            chunks = GetDistinctTrimmedCommands(commandText);

            if (chunks.Length > 1)
            {
                if (prepare || commandType == CommandType.StoredProcedure)
                {
                    throw new NpgsqlException("Multiple queries not supported for this command type");
                }
            }

            foreach (StringChunk chunk in chunks)
            {
                if (commandBuilder.Length > 0)
                {
                    commandBuilder
                        .WriteBytes((byte)ASCIIBytes.SemiColon)
                        .WriteBytes((byte)ASCIIBytes.CarriageReturn)
                        .WriteBytes((byte)ASCIIBytes.LineFeed);
                }

                if (prepare && ! forExtendQuery)
                {
                    commandBuilder
                        .WriteString("PREPARE ")
                        .WriteString(planName)
                        .WriteString(" AS ");
                }

                if (commandType == CommandType.StoredProcedure)
                {
                    if (! prepare && ! functionChecksDone)
                    {
                        functionNeedsColumnListDefinition = Parameters.Count != 0 && CheckFunctionNeedsColumnDefinitionList();

                        functionChecksDone = true;
                    }

                    commandBuilder.WriteString(
                        Connector.SupportsPrepare
                        ? "SELECT * FROM " // This syntax is only available in 7.3+ as well SupportsPrepare.
                        : "SELECT " //Only a single result return supported. 7.2 and earlier.
                    );

                    if (commandText[chunk.Begin + chunk.Length - 1] == ')')
                    {
                        AppendCommandReplacingParameterValues(commandBuilder, commandText, chunk.Begin, chunk.Length, prepare, forExtendQuery);
                    }
                    else
                    {
                        commandBuilder
                            .WriteString(commandText.Substring(chunk.Begin, chunk.Length))
                            .WriteBytes((byte)ASCIIBytes.ParenLeft);

                        if (prepare)
                        {
                            AppendParameterPlaceHolders(commandBuilder);
                        }
                        else
                        {
                            AppendParameterValues(commandBuilder);
                        }

                        commandBuilder.WriteBytes((byte)ASCIIBytes.ParenRight);
                    }

                    if (! prepare && functionNeedsColumnListDefinition)
                    {
                        AddFunctionColumnListSupport(commandBuilder);
                    }
                }
                else if (commandType == CommandType.TableDirect)
                {
                    commandBuilder
                        .WriteString("SELECT * FROM ")
                        .WriteString(commandText.Substring(chunk.Begin, chunk.Length));
                }
                else
                {
                    AppendCommandReplacingParameterValues(commandBuilder, commandText, chunk.Begin, chunk.Length, prepare, forExtendQuery);
                }
            }

            return commandBuilder.ToArray();
        }

        private StringChunk[] GetDistinctTrimmedCommands(string src)
        {
            bool inQuote = false;
            bool quoteEscape = false;
            int currCharOfs = -1;
            int currChunkBeg = 0;
            int currChunkRawLen = 0;
            int currChunkTrimLen = 0;
            List<StringChunk> chunks = new List<StringChunk>();

            foreach (char ch in src)
            {
                currCharOfs++;

                ProcessCharacter:

                if (! inQuote)
                {
                    switch (ch)
                    {
                        case '\'' :
                            inQuote = true;

                            currChunkRawLen++;
                            currChunkTrimLen = currChunkRawLen;

                            break;

                        case ';' :
                            if (currChunkTrimLen > 0)
                            {
                                chunks.Add(new StringChunk(currChunkBeg, currChunkTrimLen));
                            }

                            currChunkBeg = currCharOfs + 1;
                            currChunkRawLen = 0;
                            currChunkTrimLen = 0;

                            break;

                        case ' ' :
                        case '\t' :
                        case '\r' :
                        case '\n' :
                            if (currChunkTrimLen == 0)
                            {
                                currChunkBeg++;
                            }
                            else
                            {
                                currChunkRawLen++;
                            }

                            break;

                        default :
                            currChunkRawLen++;
                            currChunkTrimLen = currChunkRawLen;

                            break;

                    }
                }
                else
                {
                    switch (ch)
                    {
                        case '\'' :
                            if (quoteEscape)
                            {
                                quoteEscape = false;
                            }
                            else
                            {
                                quoteEscape = true;
                            }

                            currChunkRawLen++;
                            currChunkTrimLen = currChunkRawLen;

                            break;

                        default :
                            if (quoteEscape)
                            {
                                quoteEscape = false;
                                inQuote = false;

                                goto ProcessCharacter;
                            }
                            else
                            {
                                currChunkRawLen++;
                                currChunkTrimLen = currChunkRawLen;
                            }

                            break;

                    }
                }
            }

            if (currChunkTrimLen > 0)
            {
                chunks.Add(new StringChunk(currChunkBeg, currChunkTrimLen));
            }

            return chunks.ToArray();
        }

        private void AppendParameterPlaceHolders(Stream dest)
        {
            bool first = true;

            for (int i = 0; i < parameters.Count; i++)
            {
                NpgsqlParameter parameter = parameters[i];

                if (
                    (parameter.Direction == ParameterDirection.Input) ||
                    (parameter.Direction == ParameterDirection.InputOutput)
                )
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        dest.WriteString(", ");
                    }

                    AppendParameterPlaceHolder(dest, parameter, i + 1);
                }
            }
        }

        private void AppendParameterPlaceHolder(Stream dest, NpgsqlParameter parameter, int paramNumber)
        {
            string parameterSize = "";

            dest.WriteBytes((byte)ASCIIBytes.ParenLeft);

            if (parameter.TypeInfo.UseSize && (parameter.Size > 0))
            {
                parameterSize = string.Format("({0})", parameter.Size);
            }

            if (parameter.UseCast)
            {
                dest.WriteString("${0}::{1}{2}", paramNumber, parameter.TypeInfo.CastName, parameterSize);
            }
            else
            {
                dest.WriteString("${0}{1}", paramNumber, parameterSize);
            }

            dest.WriteBytes((byte)ASCIIBytes.ParenRight);
        }

        private void AppendParameterValues(Stream dest)
        {
            bool first = true;

            for (int i = 0 ; i < parameters.Count ; i++)
            {
                NpgsqlParameter parameter = parameters[i];

                if (
                    (parameter.Direction == ParameterDirection.Input) ||
                    (parameter.Direction == ParameterDirection.InputOutput)
                )
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        dest.WriteString(", ");
                    }

                    AppendParameterValue(dest, parameter);
                }
            }
        }

        private void AppendParameterValue(Stream dest, NpgsqlParameter parameter)
        {
            byte[] serialised = parameter.TypeInfo.ConvertToBackend(parameter.Value, false, Connector.NativeToBackendTypeConverterOptions);

            // Add parentheses wrapping parameter value before the type cast to avoid problems with Int16.MinValue, Int32.MinValue and Int64.MinValue
            // See bug #1010543
            // Check if this parenthesis can be collapsed with the previous one about the array support. This way, we could use
            // only one pair of parentheses for the two purposes instead of two pairs.
            dest
                .WriteBytes((byte)ASCIIBytes.ParenLeft)
                .WriteBytes((byte)ASCIIBytes.ParenLeft)
                .WriteBytes(serialised)
                .WriteBytes((byte)ASCIIBytes.ParenRight);

            if (parameter.UseCast)
            {
                dest.WriteString("::{0}", parameter.TypeInfo.CastName);

                if (parameter.TypeInfo.UseSize && (parameter.Size > 0))
                {
                    dest.WriteString("({0})", parameter.Size);
                }
            }

            dest.WriteBytes((byte)ASCIIBytes.ParenRight);
        }

        private static bool IsParamNameChar(char ch)
        {
            if (ch < '.' || ch > 'z')
            {
                return false;
            }
            else
            {
                return ((byte)ParamNameCharTable.GetValue(ch) != 0);
            }
        }

        private enum TokenType
        {
            None,
            Quoted,
            Param,
            Colon
        }

        private void AppendCommandReplacingParameterValues(Stream dest, string src, int begin, int length, bool prepare, bool forExtendedQuery)
        {
            char lastChar = '\0';
            TokenType currTokenType = TokenType.None;
            char paramMarker = '\0';
            int currTokenBeg = begin;
            int currTokenLen = 0;

            Dictionary<NpgsqlParameter, int> paramOrdinalMap = null;

            if (prepare)
            {
                paramOrdinalMap = new Dictionary<NpgsqlParameter, int>();

                for (int i = 0 ; i < parameters.Count ; i++)
                {
                    paramOrdinalMap[parameters[i]] = i + 1;
                }
            }

            for (int currCharOfs = begin ; currCharOfs < begin + length ; currCharOfs++)
            {
                char ch = src[currCharOfs];

                ProcessCharacter:

                switch (currTokenType)
                {
                    case TokenType.None :
                        switch (ch)
                        {
                            case '\'':
                                if (currTokenLen > 0)
                                {
                                    dest.WriteString(src.Substring(currTokenBeg, currTokenLen));
                                }

                                currTokenType = TokenType.Quoted;

                                currTokenBeg = currCharOfs;
                                currTokenLen = 1;

                                break;

                            case ':':
                                {
                                    dest.WriteString(src.Substring(currTokenBeg, currTokenLen));
                                }

                                currTokenType = TokenType.Colon;

                                currTokenBeg = currCharOfs;
                                currTokenLen = 1;

                                break;

                            case '@':
                                {
                                    dest.WriteString(src.Substring(currTokenBeg, currTokenLen));
                                }

                                currTokenType = TokenType.Param;

                                currTokenBeg = currCharOfs + 1;
                                currTokenLen = 0;
                                paramMarker = '@';

                                break;

                            default:
                                currTokenLen++;

                                break;

                        }

                        break;

                    case TokenType.Param :
                        if (IsParamNameChar(ch))
                        {
                            currTokenLen++;
                        }
                        else
                        {
                            if (currTokenLen == 0)
                            {
                                dest.WriteBytes((byte)ASCIIBytes.Colon);
                            }
                            else
                            {
                                string paramName = src.Substring(currTokenBeg, currTokenLen);
                                NpgsqlParameter parameter;
                                bool wroteParam = false;

                                if (parameters.TryGetValue(paramName, out parameter))
                                {
                                    if (
                                        (parameter.Direction == ParameterDirection.Input) ||
                                        (parameter.Direction == ParameterDirection.InputOutput)
                                    )
                                    {
                                        if (prepare)
                                        {
                                            AppendParameterPlaceHolder(dest, parameter, paramOrdinalMap[parameter]);
                                        }
                                        else
                                        {
                                            AppendParameterValue(dest, parameter);
                                        }
                                    }

                                    wroteParam = true;
                                }

                                if (! wroteParam)
                                {
                                    dest.WriteString("{0}{1}", paramMarker, paramName);
                                }
                            }

                            currTokenType = TokenType.None;
                            currTokenBeg = currCharOfs;
                            currTokenLen = 0;

                            // Re-evaluate this character
                            goto ProcessCharacter;
                        }

                        break;

                    case TokenType.Quoted :
                        switch (ch)
                        {
                            case '\'':
                                currTokenLen++;

                                break;

                            default:
                                if (currTokenLen > 1 && lastChar == '\'')
                                {
                                    dest.WriteString(src.Substring(currTokenBeg, currTokenLen));

                                    currTokenType = TokenType.None;
                                    currTokenBeg = currCharOfs;
                                    currTokenLen = 0;

                                    // Re-evaluate this character
                                    goto ProcessCharacter;
                                }
                                else
                                {
                                    currTokenLen++;
                                }

                                break;

                        }

                        break;

                    case TokenType.Colon :
                        switch (ch)
                        {
                            case ':':
                                currTokenLen++;

                                break;

                            default:
                                if (currTokenLen == 1)
                                {
                                    currTokenType = TokenType.Param;

                                    currTokenBeg = currCharOfs;
                                    currTokenLen = 0;
                                    paramMarker = ':';
                                }
                                else
                                {
                                    dest.WriteString(src.Substring(currTokenBeg, currTokenLen));

                                    currTokenType = TokenType.None;

                                    currTokenBeg = currCharOfs;
                                    currTokenLen = 0;
                                }

                                goto ProcessCharacter;

                        }

                        break;


                }

                lastChar = ch;
            }

            switch (currTokenType)
            {
                case TokenType.Param :
                    if (currTokenLen == 0)
                    {
                        dest.WriteBytes((byte)ASCIIBytes.Colon);
                    }
                    else
                    {
                        string paramName = src.Substring(currTokenBeg, currTokenLen);
                        NpgsqlParameter parameter;
                        bool wroteParam = false;

                        if (parameters.TryGetValue(paramName, out parameter))
                        {
                            if (
                                (parameter.Direction == ParameterDirection.Input) ||
                                (parameter.Direction == ParameterDirection.InputOutput)
                            )
                            {
                                if (prepare)
                                {
                                    AppendParameterPlaceHolder(dest, parameter, paramOrdinalMap[parameter]);
                                }
                                else
                                {
                                    AppendParameterValue(dest, parameter);
                                }
                            }

                            wroteParam = true;
                        }

                        if (! wroteParam)
                        {
                            dest.WriteString("{0}{1}", paramMarker, paramName);
                        }
                    }

                    break;

                default :
                    if (currTokenLen > 0)
                    {
                        dest.WriteString(src.Substring(currTokenBeg, currTokenLen));
                    }

                    break;

            }
        }

        private byte[] GetExecuteCommandText()
        {
            NpgsqlEventLog.LogMethodEnter(LogLevel.Debug, CLASSNAME, "GetPreparedCommandText");

            MemoryStream result = new MemoryStream();

            result.WriteString("EXECUTE {0}", planName);

            if(parameters.Count != 0)
            {
                result.WriteByte((byte)ASCIIBytes.ParenLeft);

                for (int i = 0 ; i < Parameters.Count ; i++)
                {
                    var p = Parameters[i];

                    if (i > 0)
                    {
                        result.WriteByte((byte)ASCIIBytes.Comma);
                    }

                    // Add parentheses wrapping parameter value before the type cast to avoid problems with Int16.MinValue, Int32.MinValue and Int64.MinValue
                    // See bug #1010543
                    result.WriteByte((byte)ASCIIBytes.ParenLeft);

                    byte[] serialization;

                    serialization = p.TypeInfo.ConvertToBackend(p.Value, false, Connector.NativeToBackendTypeConverterOptions);

                    result
                        .WriteBytes(serialization)
                        .WriteBytes((byte)ASCIIBytes.ParenRight);

                    if (p.UseCast)
                    {
                        PGUtil.WriteString(result, string.Format("::{0}", p.TypeInfo.CastName));

                        if (p.TypeInfo.UseSize && (p.Size > 0))
                        {
                            result.WriteString("({0})", p.Size);
                        }
                    }
                }

                result.WriteByte((byte)ASCIIBytes.ParenRight);
            }

            return result.ToArray();
        }

        private void SetCommandTimeout()
        {
            if (commandTimeoutSet)
                return;

            if (Connection != null)
            {
                timeout = Connection.CommandTimeout;
            }
            else
            {
                timeout = (int)NpgsqlConnectionStringBuilder.GetDefaultValue(Keywords.CommandTimeout);
            }
        }

        internal NpgsqlException ClearPoolAndCreateException(Exception e)
        {
            Connection.ClearPool();
            return new NpgsqlException(resman.GetString("Exception_ConnectionBroken"), e);
        }

        public override bool DesignTimeVisible
        {
            get { return designTimeVisible; }
            set { designTimeVisible = value; }
        }
    }
}
