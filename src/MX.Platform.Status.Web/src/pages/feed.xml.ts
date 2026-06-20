import type { APIRoute } from 'astro';

export const GET: APIRoute = async ({ request }) => {
  // In production, SWA rewrites /feed.xml to /api/feed.xml.
  // This endpoint exists for local dev and as a fallback.
  const apiUrl = new URL('/api/feed.xml', request.url);
  try {
    const res = await fetch(apiUrl.toString());
    return new Response(res.body, {
      status: res.status,
      headers: { 'Content-Type': 'application/rss+xml; charset=utf-8' },
    });
  } catch {
    return new Response('<rss version="2.0"><channel><title>Status</title></channel></rss>', {
      status: 503,
      headers: { 'Content-Type': 'application/rss+xml; charset=utf-8' },
    });
  }
};
