export const CATEGORY_COLORS: Record<string, string> = {
  Person: '#dc2626',
  PersonType: '#ea580c',
  PhoneNumber: '#d97706',
  Organization: '#7c3aed',
  Address: '#0369a1',
  Email: '#0891b2',
  URL: '#0891b2',
  IPAddress: '#0891b2',
  DateTime: '#15803d',
  Date: '#15803d',
  Quantity: '#166534',
  Age: '#16a34a',
  USSocialSecurityNumber: '#be123c',
  Default: '#6b7280',
};

export function categoryColor(category: string): string {
  return CATEGORY_COLORS[category] ?? CATEGORY_COLORS['Default'];
}
