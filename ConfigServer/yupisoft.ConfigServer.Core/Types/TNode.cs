﻿using Newtonsoft.Json.Linq;
using System.Dynamic;

namespace yupisoft.ConfigServer.Core
{    
    public class TNode
    {
        private string _path;
        private JToken _value;
        private string _entity;

        public TNode() {
            _path = "";
            _value = null;
            _entity = "";
        }

        public TNode(string path, JToken value, string entityName)
        {
            this._path = path;
            this._value = value;
            this._entity = entityName;
        }
        public string Path => _path;
        public JToken Value => _value;
        public string Entity => _entity;
    }

}
