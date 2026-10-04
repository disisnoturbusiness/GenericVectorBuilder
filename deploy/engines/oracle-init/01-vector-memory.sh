#!/bin/bash
# Runs on every start of the gvb-oracle container, after the database is up (the image runs
# everything in /container-entrypoint-startdb.d at that point).
# Gives the database a vector memory pool, which HNSW (in-memory neighbor graph) vector indexes
# live in. Oracle Free ships with the pool at 0, and CREATE VECTOR INDEX ... ORGANIZATION INMEMORY
# NEIGHBOR GRAPH is refused until it is set. The parameter is static: it is written to the spfile
# (which sits in the mounted data directory, so it sticks) and takes effect only after a restart of
# the instance, so on the first start this script bounces the database once. Later starts see the
# pool is already there and do nothing.
# 768 MB: Free caps the SGA at 1.5 GB and the pool is carved out of it, so this is about half;
# 100,000 vectors of 1024 float32 dimensions need roughly 410 MB for the vectors plus the graph.

POOL_SIZE=768M

current=$(sqlplus -s / as sysdba <<SQL
set heading off feedback off pagesize 0 verify off
select value from v\$parameter where name = 'vector_memory_size';
exit
SQL
)
current=$(echo "${current}" | tr -d '[:space:]')

if [ "${current}" = "0" ]; then
   echo "CONTAINER: vector_memory_size is 0, setting it to ${POOL_SIZE} and restarting the instance once."
   sqlplus -s / as sysdba <<SQL
alter system set vector_memory_size=${POOL_SIZE} scope=spfile;
shutdown immediate
startup
alter pluggable database all open;
exit
SQL
else
   echo "CONTAINER: vector_memory_size is already ${current} bytes, nothing to do."
fi
