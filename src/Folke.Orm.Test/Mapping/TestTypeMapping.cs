﻿using System.ComponentModel.DataAnnotations;
using System.Linq;
using Folke.Orm.Mapping;
using Moq;
using Xunit;

namespace Folke.Orm.Test.Mapping
{
    public class TestTypeMapping
    {
        private Mock<IMapper> mapperMock;

        public TestTypeMapping()
        {
            mapperMock = new Mock<IMapper>();
        }

        [Fact]
        public void TypeMapping_Constructor_TypeWithGenericProperties()
        {
            var referencedTypeMapping = new TypeMapping(typeof(GenericClass<string>), mapperMock.Object);
            mapperMock.Setup(x => x.GetTypeMapping(typeof(GenericClass<string>))).Returns(referencedTypeMapping);
            mapperMock.Setup(x => x.IsMapped(typeof(GenericClass<string>))).Returns(true);

            var typeMapping = new TypeMapping(typeof(TypeWithGenericProperties<string>), mapperMock.Object);
            
            Assert.True(typeMapping.Columns.Any(x => x.Key == "Id"));
            Assert.True(typeMapping.Columns.Any(x => x.Key == "GenericProperty"));
            Assert.True(typeMapping.Columns.Any(x => x.Value.ColumnName == "GenericProperty_id"));
        }

        public class GenericClass<TKey>
        {
            [Key]
            public TKey Id { get; set; }
        }

        public class TypeWithGenericProperties<TKey>
        {
            public int Id { get; set; }

            public GenericClass<TKey> GenericProperty { get; set; }
        }
    }
}
