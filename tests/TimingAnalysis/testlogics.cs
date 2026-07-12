using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DocumentGeneratorTest
{
    public class OrderProcessingSystem
    {
        private readonly List<Product> _inventory = new();
        private readonly List<Order> _orders = new();

        /// <summary>
        /// 1. Simple Validation Logic
        /// Checks if an order meets basic business rules before processing.
        /// </summary>
        public bool ValidateOrder(Order order)
        {
            if (order == null) return false;
            if (order.Id <= 0) return false;
            if (!order.Items.Any()) return false;
            
            bool hasValidCustomer = !string.IsNullOrWhiteSpace(order.CustomerEmail);
            bool hasPositiveAmounts = order.Items.All(item => item.Quantity > 0 && item.Price > 0);

            return hasValidCustomer && hasPositiveAmounts;
        }

        /// <summary>
        /// 2. Iterative Calculation Logic
        /// Computes the total cost of an order, applying bulk discounts and tax rates.
        /// </summary>
        public decimal CalculateOrderTotal(Order order, decimal taxRate, decimal bulkDiscountThreshold)
        {
            decimal subtotal = 0;

            foreach (var item in order.Items)
            {
                decimal itemTotal = item.Price * item.Quantity;
                if (item.Quantity >= 5)
                {
                    itemTotal *= 0.90m; // 10% bulk discount for individual item line
                }
                subtotal += itemTotal;
            }

            if (subtotal > bulkDiscountThreshold)
            {
                subtotal -= 20.00m; // Flat discount for massive orders
            }

            decimal totalTax = subtotal * (taxRate / 100);
            return Math.Round(subtotal + totalTax, 2);
        }

        /// <summary>
        /// 3. Data Transformation & LINQ
        /// Extracts and groups high-value customers based on their spending history.
        /// </summary>
        public Dictionary<string, decimal> GetTopSpenders(decimal minimumSpent)
        {
            return _orders
                .Where(o => o.IsCompleted)
                .GroupBy(o => o.CustomerEmail)
                .Select(g => new { Email = g.Key, TotalSpent = g.Sum(o => o.TotalAmount) })
                .Where(x => x.TotalSpent >= minimumSpent)
                .OrderByDescending(x => x.TotalSpent)
                .ToDictionary(x => x.Email, x => x.TotalSpent);
        }

        /// <summary>
        /// 4. Asynchronous Network / Database Simulation
        /// Fetches inventory updates from an external mock API.
        /// </summary>
        public async Task<bool> RefreshInventoryAsync(string apiEndpoint)
        {
            try
            {
                // Simulating network latency
                await Task.Delay(1500);

                if (string.IsNullOrEmpty(apiEndpoint))
                {
                    throw new ArgumentException("API endpoint cannot be empty.");
                }

                // Mocking a successful data sync
                _inventory.Clear();
                _inventory.Add(new Product { Id = 101, Name = "Laptop", Stock = 15 });
                _inventory.Add(new Product { Id = 102, Name = "Mouse", Stock = 120 });
                
                return true;
            }
            catch (Exception)
            {
                // In a real app, log the exception here
                return false;
            }
        }

        /// <summary>
        /// 5. State Mutation & Inventory Control
        /// Deducts items from inventory, handling out-of-stock edge cases safely.
        /// </summary>
        public List<string> FulfillInventory(Order order)
        {
            var missingItemsReport = new List<string>();

            lock (_inventory)
            {
                foreach (var item in order.Items)
                {
                    var stockItem = _inventory.FirstOrDefault(p => p.Id == item.ProductId);
                    if (stockItem == null || stockItem.Stock < item.Quantity)
                    {
                        missingItemsReport.Add($"Product ID {item.ProductId} is short on stock.");
                    }
                    else
                    {
                        stockItem.Stock -= item.Quantity;
                    }
                }
            }

            return missingItemsReport;
        }

        /// <summary>
        /// 6. Conditional Rule-Based Flagging
        /// Evaluates risk levels for transactions based on thresholds and location matching.
        /// </summary>
        public string EvaluateFraudRisk(Order order, string systemIpCountry)
        {
            int riskScore = 0;

            if (order.TotalAmount > 5000) riskScore += 40;
            if (order.ShippingCountry != systemIpCountry) riskScore += 30;
            if (order.CustomerEmail.EndsWith(".tempomail.com")) riskScore += 25;

            if (riskScore >= 70) return "HIGH RISK - Flagged for manual review";
            if (riskScore >= 30) return "MEDIUM RISK - Require multi-factor auth";
            
            return "LOW RISK";
        }

        /// <summary>
        /// 7. Pattern Matching & Strategy Selection
        /// Formats invoices dynamically based on the requested file type.
        /// </summary>
        public string FormatInvoice(Order order, ExportFormat format)
        {
            return format switch
            {
                ExportFormat.Csv => $"OrderId,Customer,Total\n{order.Id},{order.CustomerEmail},{order.TotalAmount}",
                ExportFormat.Json => $"{{\"OrderId\": {order.Id}, \"Customer\": \"{order.CustomerEmail}\", \"Total\": {order.TotalAmount}}}",
                ExportFormat.PlainText => $"Invoice for {order.CustomerEmail}\nOrder #{order.Id}\nTotal: ${order.TotalAmount}",
                _ => throw new NotSupportedException($"The format {format} is not supported.")
            };
        }

        /// <summary>
        /// 8. Recursive Hierarchy Search
        /// Walks through a category tree structure to find if a subcategory belongs to a parent.
        /// </summary>
        public bool IsSubcategoryOf(Category current, string targetParentName)
        {
            if (current == null) return false;
            if (current.Name.Equals(targetParentName, StringComparison.OrdinalIgnoreCase)) return true;
            if (current.Parent == null) return false;

            return IsSubcategoryOf(current.Parent, targetParentName);
        }

        /// <summary>
        /// 9. String Formatting & Token Replacement
        /// Generates custom notification emails using bracketed placeholders.
        /// </summary>
        public string GenerateNotificationEmail(string template, Order order, string statusReason)
        {
            if (string.IsNullOrWhiteSpace(template)) return string.Empty;

            string emailBody = template
                .Replace("[CustomerEmail]", order.CustomerEmail)
                .Replace("[OrderId]", order.Id.ToString())
                .Replace("[TotalAmount]", order.TotalAmount.ToString("C"))
                .Replace("[Reason]", statusReason ?? "N/A")
                .Replace("[Date]", DateTime.UtcNow.ToShortDateString());

            return emailBody.Trim();
        }

        /// <summary>
        /// 10. Exception Heavy & Resource Disposal Simulation
        /// Processes a database batch transaction mockup with explicit try/catch/finally blocks.
        /// </summary>
        public bool TryBatchInsertOrders(List<Order> batch)
        {
            bool executionSuccess = false;
            Console.WriteLine("Opening mock database connection...");

            try
            {
                if (batch == null || !batch.Any())
                {
                    throw new InvalidOperationException("Cannot process an empty batch.");
                }

                foreach (var order in batch)
                {
                    _orders.Add(order);
                }

                executionSuccess = true;
            }
            catch (InvalidOperationException ioEx)
            {
                Console.WriteLine($"Batch operational failure: {ioEx.Message}");
                executionSuccess = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal unexpected database error: {ex.Message}");
                executionSuccess = false;
            }
            finally
            {
                Console.WriteLine("Closing mock database connection to avoid memory leaks.");
            }

            return executionSuccess;
        }
    }

    #region Supporting Mock Classes
    public class Order
    {
        public int Id { get; set; }
        public string CustomerEmail { get; set; } = string.Empty;
        public string ShippingCountry { get; set; } = string.Empty;
        public List<OrderItem> Items { get; set; } = new();
        public decimal TotalAmount { get; set; }
        public bool IsCompleted { get; set; }
    }

    public class OrderItem
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
    }

    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Stock { get; set; }
    }

    public class Category
    {
        public string Name { get; set; } = string.Empty;
        public Category? Parent { get; set; }
    }

    public enum ExportFormat
    {
        PlainText,
        Csv,
        Json
    }
    #endregion
}