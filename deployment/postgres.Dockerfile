FROM postgres:18-bookworm AS postgres

FROM scratch
COPY --from=postgres / /
ENV PATH=/usr/lib/postgresql/18/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    LANG=en_US.utf8 PGDATA=/var/lib/postgresql/data
COPY --chmod=0555 deployment/postgres-entrypoint.sh /usr/local/bin/status-postgres
USER 999:10001
EXPOSE 5432
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=3 \
    CMD ["pg_isready", "--host=127.0.0.1", "--username=status_app", "--dbname=status"]
ENTRYPOINT ["/usr/local/bin/status-postgres"]
