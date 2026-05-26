using LaraFashion.Models;
using LaraFashion.Models.Enums;

namespace LaraFashion.Data;

public static class DummyData
{
    public static List<Product> Products => new()
    {
        new Product
        {
            Id = Guid.NewGuid(),
            Name = "Pink Dress",
            SerialNumber = "LR-1001",
            Description = "Beautiful pink dress",
            OriginalPrice = 80,
            DiscountType = DiscountType.Percent,
            DiscountValue = 25,
            ImageUrl = "https://images.unsplash.com/photo-1515886657613-9f3515b0c78f?q=80&w=1200&auto=format&fit=crop",
            Sizes = new()
                        {
                            new ProductSize
                            {
                                SizeName = "S",
                                Quantity = 5
                            },

                            new ProductSize
                            {
                                SizeName = "M",
                                Quantity = 8
                            },

                            new ProductSize
                            {
                                SizeName = "L",
                                Quantity = 3
                            }
                        },
        },

        new Product
        {
            Id = Guid.NewGuid(),
            Name = "Blue Kids Shirt",
            SerialNumber = "LR-1002",
            Description = "Kids fashion shirt",
            OriginalPrice = 40,
            DiscountType = DiscountType.None,
            DiscountValue = 0,
            ImageUrl = "https://images.unsplash.com/photo-1521572267360-ee0c2909d518?q=80&w=1200&auto=format&fit=crop",
            Sizes = new()
                        {
                            new ProductSize
                            {
                                SizeName = "S",
                                Quantity = 5
                            },

                            new ProductSize
                            {
                                SizeName = "M",
                                Quantity = 8
                            },

                            new ProductSize
                            {
                                SizeName = "L",
                                Quantity = 3
                            }
                        },
        },

        new Product
        {
            Id = Guid.NewGuid(),
            Name = "Winter Jacket",
            SerialNumber = "LR-1003",
            Description = "Warm winter jacket",
            OriginalPrice = 120,
            DiscountType = DiscountType.FixedPrice,
            DiscountValue = 90,
            ImageUrl = "https://images.unsplash.com/photo-1541099649105-f69ad21f3246?q=80&w=1200&auto=format&fit=crop",
            Sizes = new()
                        {
                            new ProductSize
                            {
                                SizeName = "S",
                                Quantity = 5
                            },
                            new ProductSize
                            {
                                SizeName = "M",
                                Quantity = 8
                            },
                            new ProductSize
                            {
                                SizeName = "L",
                                Quantity = 3
                            }
                        },
        }
    };
}