﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Sql
{
    using System.Collections.Generic;

    internal abstract class SqlObject
    {
        protected SqlObject(SqlObjectKind kind)
        {
            this.Kind = kind;
        }

        public SqlObjectKind Kind
        {
            get;
        }

        public abstract void Accept(SqlObjectVisitor visitor);

        public abstract TResult Accept<TResult>(SqlObjectVisitor<TResult> visitor);

        public abstract TResult Accept<T, TResult>(SqlObjectVisitor<T, TResult> visitor, T input);

        public override string ToString()
        {
            return this.Serialize(prettyPrint: false);
        }

        public override int GetHashCode()
        {
            return this.Accept(SqlObjectHasher.Singleton);
        }

        public string PrettyPrint()
        {
            return this.Serialize(prettyPrint: true);
        }

        public SqlObject GetObfuscatedObject()
        {
            SqlObjectObfuscator sqlObjectObfuscator = new SqlObjectObfuscator();
            return this.Accept(sqlObjectObfuscator);
        }

        public string ToParamterizedString(IDictionary<object, string> parameters, bool prettyPrint = false)
        {
            SqlObjectParameterizedTextSerializer sqlObjectParameterizedTextSerializer = new SqlObjectParameterizedTextSerializer(prettyPrint, parameters);
            this.Accept(sqlObjectParameterizedTextSerializer);
            return sqlObjectParameterizedTextSerializer.ToString();
        }

        private string Serialize(bool prettyPrint)
        {
            SqlObjectTextSerializer sqlObjectTextSerializer = new SqlObjectTextSerializer(prettyPrint);
            this.Accept(sqlObjectTextSerializer);
            return sqlObjectTextSerializer.ToString();
        }
    }
}
