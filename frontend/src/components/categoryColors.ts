const CATEGORY_COLORS: Record<string, string> = {
  Person: '#dc2626',
  PersonType: '#ea580c',
  Organization: '#7c3aed',
  Address: '#2563eb',
  PhoneNumber: '#0891b2',
  Email: '#0f766e',
  DateTime: '#ca8a04',
  Quantity: '#9333ea',
  IPAddress: '#4f46e5',
  URL: '#0284c7',
};

export function categoryColor(category: string): string {
  return CATEGORY_COLORS[category] ?? '#4b5563';
}
