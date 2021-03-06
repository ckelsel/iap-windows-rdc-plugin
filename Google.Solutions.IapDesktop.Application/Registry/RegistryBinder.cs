﻿//
// Copyright 2020 Google LLC
//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using Google.Apis.Util;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Google.Solutions.IapDesktop.Application.Registry
{
    /// <summary>
    /// This class performs data binding between an object and a registry key
    /// by mapping properties to registry values.
    /// </summary>
    /// <typeparam name="TDataClass"></typeparam>
    public class RegistryBinder<TDataClass> where TDataClass : new()
    {
        public IEnumerable<string> ValueNames =>
            typeof(TDataClass).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(property => property.GetCustomAttributes(true))
                .OfType<RegistryValueAttribute>()
                .Where(attribute => attribute.Name != null)
                .Select(attribute => attribute.Name);

        private PropertyInfo GetPropertyByValueName(string valueName)
        {
            return typeof(TDataClass).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(property => property
                    .GetCustomAttributes()
                    .OfType<RegistryValueAttribute>()
                    .Any(attribute => attribute.Name == valueName));
        }

        private object GetValue(TDataClass obj, PropertyInfo property)
        {
            if (property == null)
            {
                throw new ArgumentException(nameof(property));
            }
            else
            {
                return property.GetCustomAttributes()
                    .OfType<RegistryValueAttribute>()
                    .FirstOrDefault()
                    .GetValue(obj, property);
            }
        }

        private void SetValue(TDataClass obj, PropertyInfo property, object value)
        {
            Utilities.ThrowIfNull(obj, nameof(obj));
            Utilities.ThrowIfNull(property, nameof(property));

            property.GetCustomAttributes()
                .OfType<RegistryValueAttribute>()
                .FirstOrDefault()
                .SetValue(obj, property, value);
        }

        public void Store(TDataClass source, RegistryKey target)
        {
            Utilities.ThrowIfNull(source, nameof(source));
            Utilities.ThrowIfNull(target, nameof(target));

            foreach (var valueName in this.ValueNames)
            {
                var property = GetPropertyByValueName(valueName);
                object value = GetValue(source, property);
                if (value == null)
                {
                    target.DeleteValue(valueName, false);
                }
                else
                {
                    target.SetValue(
                        valueName,
                        value,
                        property.GetCustomAttributes<RegistryValueAttribute>().First().Kind);
                }
            }
        }

        public TDataClass Load(RegistryKey source)
        {
            Utilities.ThrowIfNull(source, nameof(source));

            var target = new TDataClass();

            foreach (var valueName in this.ValueNames)
            {
                var property = GetPropertyByValueName(valueName);
                SetValue(target, property, source.GetValue(valueName));
            }

            return target;
        }
    }
}
